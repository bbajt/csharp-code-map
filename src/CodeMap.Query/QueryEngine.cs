namespace CodeMap.Query;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Extensions.Logging;
using RefKind = CodeMap.Core.Enums.RefKind;

/// <summary>
/// Orchestrates storage queries, budget enforcement, caching, and envelope assembly.
/// Implements IQueryEngine for Milestone 01 (Committed consistency mode only).
/// </summary>
public sealed class QueryEngine : IQueryEngine
{
    private readonly ISymbolStore _store;
    private readonly ICacheService _cache;
    private readonly ITokenSavingsTracker _tracker;
    private readonly ExcerptReader _excerptReader;
    private readonly GraphTraverser _graphTraverser;
    private readonly FeatureTracer _featureTracer;
    private readonly ILogger<QueryEngine> _logger;
    private readonly IMetadataResolver? _metadataResolver;

    private static readonly IReadOnlyDictionary<string, LimitApplied> _noLimits =
        new Dictionary<string, LimitApplied>(0);

    public QueryEngine(
        ISymbolStore store,
        ICacheService cache,
        ITokenSavingsTracker tracker,
        ExcerptReader excerptReader,
        GraphTraverser graphTraverser,
        FeatureTracer featureTracer,
        ILogger<QueryEngine> logger,
        IMetadataResolver? metadataResolver = null)
    {
        _store = store;
        _cache = cache;
        _tracker = tracker;
        _excerptReader = excerptReader;
        _graphTraverser = graphTraverser;
        _featureTracer = featureTracer;
        _logger = logger;
        _metadataResolver = metadataResolver;
    }

    // ─── SearchSymbolsAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>> SearchSymbolsAsync(
        RoutingContext routing,
        string? query,
        SymbolSearchFilters? filters,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        // 1. Validate / route
        if (string.IsNullOrWhiteSpace(query))
        {
            if (filters?.Kinds is { Count: > 0 })
                return await BrowseByKindsAsync(routing, filters, budgets, ct).ConfigureAwait(false);
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument(
                    "query is required when no kinds filter is specified. " +
                    "To browse by type, omit query and pass a kinds filter (e.g. kinds=[\"Class\"])."));
        }

        var sanitized = FtsQuerySanitizer.Sanitize(query) ?? "";
        if (string.IsNullOrEmpty(sanitized))
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument("Query contains only unsupported FTS5 special characters. Try a plain symbol name."));
        query = sanitized;

        // 2. Resolve commit
        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        // 3. Ensure baseline
        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(baselineResult.Error);

        // 4. Resolve budgets
        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var maxResults = clamped.MaxResults;

        // 5. Check cache
        var cacheKey = BuildSearchCacheKey(routing.RepoId, commitSha, query, filters, maxResults);
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SymbolSearchResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for search query '{Query}'", query);
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(cached);
        }

        // 6. Execute storage query (request one extra to detect truncation)
        tc.StartPhase();
        var hits = await _store.SearchSymbolsAsync(routing.RepoId, commitSha, query, filters, maxResults + 1, ct);
        tc.EndDbQuery();

        var truncated = hits.Count > maxResults;
        if (truncated)
            hits = hits.Take(maxResults).ToList();

        var totalCount = truncated ? maxResults + 1 : hits.Count;

        // 7. Build response
        var data = new SymbolSearchResponse(hits, totalCount, truncated);
        var answer = AnswerGenerator.ForSearch(hits, query, truncated);
        var nextActions = NextActionsForSearch(hits);
        IReadOnlyList<EvidencePointer> evidence = [];

        // 8. Token savings
        var tokensSaved = TokenSavingsEstimator.ForSearch(hits.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        var costPerModel = TokenSavingsEstimator.EstimateCostPerModel(tokensSaved);
        _tracker.RecordSaving(tokensSaved, costPerModel);

        // 9. Build timing
        var timing = tc.Build();

        // 10. Assemble envelope
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, evidence, nextActions,
            Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        // 11. Cache result
        await _cache.SetAsync(cacheKey, envelope, ct);

        return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(envelope);
    }

    // ─── BrowseByKindsAsync (no-query path) ──────────────────────────────────

    private async Task<Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>> BrowseByKindsAsync(
        RoutingContext routing,
        SymbolSearchFilters filters,
        BudgetLimits? budgets,
        CancellationToken ct)
    {
        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(baselineResult.Error);

        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var maxResults = clamped.MaxResults;

        var tc = new TimingContext();
        tc.StartPhase();
        var hits = await _store.GetSymbolsByKindsAsync(
            routing.RepoId, commitSha, filters.Kinds!, maxResults + 1, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        var truncated = hits.Count > maxResults;
        if (truncated)
            hits = hits.Take(maxResults).ToList();
        var totalCount = truncated ? maxResults + 1 : hits.Count;

        var kindLabel = string.Join(", ", filters.Kinds!);
        var answer = truncated
            ? $"Found {totalCount}+ {kindLabel} symbols (showing first {maxResults}). Use a query or namespace filter to narrow results."
            : $"Found {hits.Count} {kindLabel} symbol(s).";

        var nextActions = NextActionsForSearch(hits);
        var tokensSaved = TokenSavingsEstimator.ForSearch(hits.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        var costPerModel = TokenSavingsEstimator.EstimateCostPerModel(tokensSaved);
        _tracker.RecordSaving(tokensSaved, costPerModel);

        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            new SymbolSearchResponse(hits, totalCount, truncated),
            answer, [], nextActions,
            Confidence.High, tc.Build(), limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        return Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(envelope);
    }

    // ─── GetSymbolCardAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolCardAsync(
        RoutingContext routing,
        SymbolId symbolId,
        CancellationToken ct = default)
        => GetSymbolCardAsync(routing, symbolId, includeCode: true, ct);

    /// <summary>
    /// Fetches a symbol card. When <paramref name="includeCode"/> is <c>true</c> and the
    /// card is a metadata stub (<c>IsDecompiled==1</c>), triggers Level 2 decompilation
    /// via <see cref="IMetadataResolver.TryDecompileTypeAsync"/> and re-fetches the upgraded card.
    /// </summary>
    public async Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolCardAsync(
        RoutingContext routing,
        SymbolId symbolId,
        bool includeCode,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(baselineResult.Error);

        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:card:{symbolId.Value}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SymbolCard>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for card '{SymbolId}'", symbolId.Value);
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(cached);
        }

        tc.StartPhase();
        var card = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct);

        if (card is null && _metadataResolver is not null)
        {
            // Level 1: try to extract metadata stubs from DLL references
            int inserted = await _metadataResolver.TryResolveTypeAsync(
                symbolId, routing.RepoId, commitSha, ct).ConfigureAwait(false);

            if (inserted > 0)
                card = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct);
        }

        if (card is null)
        {
            tc.EndDbQuery();
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolId.Value));
        }

        // Level 2: trigger decompilation when card is a metadata stub and code was requested
        if (card.IsDecompiled == 1 && includeCode && _metadataResolver is not null)
        {
            string? virtualPath = await _metadataResolver.TryDecompileTypeAsync(
                symbolId, routing.RepoId, commitSha, ct).ConfigureAwait(false);

            if (virtualPath is not null)
            {
                // Re-fetch card — is_decompiled is now 2, file_id updated
                card = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct)
                       ?? card; // fallback to existing stub if re-fetch fails
            }
        }

        // Hydrate Facts from stored facts for this symbol
        var storedFacts = await _store.GetFactsForSymbolAsync(routing.RepoId, commitSha, symbolId, ct);
        tc.EndDbQuery();

        if (storedFacts?.Count > 0)
            card = card with { Facts = storedFacts.Select(f => new Core.Models.Fact(f.Kind, f.Value)).ToList() };

        var evidence = BuildSymbolEvidence(routing.RepoId, card);
        var answer = AnswerGenerator.ForCard(card);
        var nextActions = NextActionsForCard(card);

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var noLimits = _noLimits;
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            card, answer, evidence, nextActions,
            card.Confidence, timing, noLimits,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        await _cache.SetAsync(cacheKey, envelope, ct);

        return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope);
    }

    // ─── GetSpanAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SpanResponse>, CodeMapError>> GetSpanAsync(
        RoutingContext routing,
        FilePath filePath,
        int startLine,
        int endLine,
        int contextLines,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        // 1. Validate inputs
        if (startLine < 1)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument("startLine must be >= 1."));
        if (endLine < startLine)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument("endLine must be >= startLine."));

        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(baselineResult.Error);

        // 3. Resolve budgets
        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();

        // 4. Apply context lines
        var effectiveStart = Math.Max(1, startLine - contextLines);
        var effectiveEnd = endLine + contextLines;

        // 5. Enforce MaxLines budget
        var requestedLines = effectiveEnd - effectiveStart + 1;
        if (requestedLines > clamped.MaxLines)
        {
            limitsApplied["MaxLines"] = new LimitApplied(requestedLines, clamped.MaxLines);
            effectiveEnd = effectiveStart + clamped.MaxLines - 1;
        }

        // 6. Check cache
        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:span:{filePath.Value}:{effectiveStart}-{effectiveEnd}:ctx{contextLines}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SpanResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for span '{FilePath}'", filePath.Value);
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(cached);
        }

        // 7. Get span from store
        tc.StartPhase();
        var fileSpan = await _store.GetFileSpanAsync(routing.RepoId, commitSha, filePath, effectiveStart, effectiveEnd, ct);
        tc.EndDbQuery();

        if (fileSpan is null)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("File", filePath.Value));

        // 8. Enforce MaxChars budget
        var content = fileSpan.Content;
        var truncated = fileSpan.Truncated;
        if (content.Length > clamped.MaxChars)
        {
            content = content[..clamped.MaxChars];
            truncated = true;
            limitsApplied["MaxChars"] = new LimitApplied(fileSpan.Content.Length, clamped.MaxChars);
        }

        // 9. Map to SpanResponse
        var data = new SpanResponse(
            filePath,
            fileSpan.StartLine,
            fileSpan.EndLine,
            fileSpan.TotalFileLines,
            content,
            truncated);

        var answer = AnswerGenerator.ForSpan(data);
        var nextActions = NextActionsForSpan();
        IReadOnlyList<EvidencePointer> evidence = [];

        var tokensSaved = TokenSavingsEstimator.ForSpan(fileSpan.TotalFileLines, fileSpan.EndLine - fileSpan.StartLine + 1);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, evidence, nextActions,
            Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        await _cache.SetAsync(cacheKey, envelope, ct);

        return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(envelope);
    }

    // ─── GetDefinitionSpanAsync ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SpanResponse>, CodeMapError>> GetDefinitionSpanAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int maxLines,
        int contextLines,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(baselineResult.Error);

        // 2. Check cache (before store call — key is deterministic from symbolId+maxLines+contextLines)
        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:defspan:{symbolId.Value}:{maxLines}:ctx{contextLines}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SpanResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for definition span '{SymbolId}'", symbolId.Value);
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(cached);
        }

        // 3. Get symbol
        var card = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct);
        if (card is null)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolId.Value));

        if (card.FilePath.Value == "unknown")
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol source",
                    $"Symbol '{symbolId.Value}' has no source location (metadata or decompiled assembly). Use symbols.get_card with include_code=true instead."));

        // 4. Compute span boundaries (clamp to maxLines)
        var spanStart = card.SpanStart;
        var spanEnd = card.SpanEnd;
        if (spanEnd - spanStart + 1 > maxLines)
            spanEnd = spanStart + maxLines - 1;

        // Apply context lines
        var effectiveStart = Math.Max(1, spanStart - contextLines);
        var effectiveEnd = spanEnd + contextLines;

        // 5. Get span from store
        tc.StartPhase();
        var fileSpan = await _store.GetFileSpanAsync(routing.RepoId, commitSha, card.FilePath, effectiveStart, effectiveEnd, ct);
        tc.EndDbQuery();

        if (fileSpan is null)
            return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("File", card.FilePath.Value));

        var data = new SpanResponse(
            card.FilePath,
            fileSpan.StartLine,
            fileSpan.EndLine,
            fileSpan.TotalFileLines,
            fileSpan.Content,
            fileSpan.Truncated);

        var answer = AnswerGenerator.ForDefinitionSpan(card, data);
        var nextActions = NextActionsForSpan();
        IReadOnlyList<EvidencePointer> evidence = [];

        var tokensSaved = TokenSavingsEstimator.ForSpan(fileSpan.TotalFileLines, fileSpan.EndLine - fileSpan.StartLine + 1);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var noLimits = _noLimits;
        var semanticLevelDef = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, evidence, nextActions,
            Confidence.High, timing, noLimits,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevelDef);

        await _cache.SetAsync(cacheKey, envelope, ct);

        return Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(envelope);
    }

    // ─── FindReferencesAsync ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>> FindReferencesAsync(
        RoutingContext routing,
        SymbolId symbolId,
        RefKind? kind,
        BudgetLimits? budgets,
        CancellationToken ct = default,
        ResolutionState? resolutionState = null)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Failure(baselineResult.Error);

        // Resolve budgets
        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var maxRefs = clamped.MaxReferences;

        // Check cache
        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:refs:{symbolId.Value}:k={kind}:lim={maxRefs}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<FindRefsResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for refs '{SymbolId}'", symbolId.Value);
            return Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(cached);
        }

        // Verify symbol exists
        var card = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct);
        if (card is null)
            return Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolId.Value));

        // Query refs (request one extra to detect truncation)
        tc.StartPhase();
        var storedRefs = await _store.GetReferencesAsync(
            routing.RepoId, commitSha, symbolId, kind, maxRefs + 1, ct, resolutionState).ConfigureAwait(false);
        tc.EndDbQuery();

        var truncated = storedRefs.Count > maxRefs;
        if (truncated)
            storedRefs = storedRefs.Take(maxRefs).ToList();

        // Add one-line excerpts
        var classified = new List<ClassifiedReference>(storedRefs.Count);
        foreach (var r in storedRefs)
        {
            var excerpt = await _excerptReader.ReadLineAsync(
                routing.RepoId, commitSha, r.FilePath, r.LineStart, ct).ConfigureAwait(false);
            classified.Add(new ClassifiedReference(r.Kind, r.FromSymbol, r.FilePath, r.LineStart, r.LineEnd, excerpt,
                r.ResolutionState, r.ToName, r.ToContainerHint));
        }

        // Build response
        var data = new FindRefsResponse(symbolId, classified, truncated ? maxRefs + 1 : classified.Count, truncated);
        var answer = AnswerGenerator.ForFindRefs(symbolId, classified.Count, kind, truncated);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_card",
                $"Get full details for {card.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = symbolId.Value })
        };

        var tokensSaved = TokenSavingsEstimator.ForSearch(classified.Count); // proxy: ref list ~ search result list
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        IReadOnlyDictionary<string, LimitApplied> limits = limitsApplied;
        var semanticLevelRefs = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, limits,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevelRefs);

        await _cache.SetAsync(cacheKey, envelope, ct);
        return Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(envelope);
    }

    // ─── GetCallersAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> GetCallersAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        CancellationToken ct = default)
        => TraverseGraphAsync(routing, symbolId, depth, limitPerLevel, budgets, direction: "callers",
            expandNode: async (sid, commitSha, clampedLimit, token) =>
            {
                var refs = await _store.GetReferencesAsync(
                    routing.RepoId, commitSha, sid, null, clampedLimit * 2, token).ConfigureAwait(false);
                return refs
                    .Where(r => (r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate) && r.FromSymbol != SymbolId.Empty)
                    .Select(r => r.FromSymbol)
                    .Distinct()
                    .Take(clampedLimit)
                    .ToList();
            }, ct);

    // ─── GetCalleesAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> GetCalleesAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        CancellationToken ct = default)
        => TraverseGraphAsync(routing, symbolId, depth, limitPerLevel, budgets, direction: "callees",
            expandNode: async (sid, commitSha, clampedLimit, token) =>
            {
                var refs = await _store.GetOutgoingReferencesAsync(
                    routing.RepoId, commitSha, sid, null, clampedLimit * 2, token).ConfigureAwait(false);
                return refs
                    .Where(r => (r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate) && r.ToSymbol != SymbolId.Empty)
                    .Select(r => r.ToSymbol)
                    .Distinct()
                    .Take(clampedLimit)
                    .ToList();
            }, ct);

    private async Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> TraverseGraphAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        string direction,
        Func<SymbolId, CommitSha, int, CancellationToken, Task<IReadOnlyList<SymbolId>>> expandNode,
        CancellationToken ct)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Failure(baselineResult.Error);

        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var clampedDepth = Math.Min(depth, clamped.MaxDepth);
        var clampedLimit = Math.Min(limitPerLevel, clamped.MaxReferences);

        // Cache check
        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:{direction}:{symbolId.Value}:d={clampedDepth}:lim={clampedLimit}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<CallGraphResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for {Direction} '{SymbolId}'", direction, symbolId.Value);
            return Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(cached);
        }

        // Verify root symbol exists
        var rootCard = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct);
        if (rootCard is null)
            return Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolId.Value));

        // BFS traversal — query limit+1 rows so the traverser can detect truncation
        tc.StartPhase();
        var traversal = await _graphTraverser.TraverseAsync(
            symbolId,
            (sid, token) => expandNode(sid, commitSha, clampedLimit + 1, token),
            clampedDepth, clampedLimit, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        // Hydrate nodes with symbol info
        var graphNodes = await HydrateNodesAsync(routing.RepoId, commitSha, traversal.Nodes, ct);

        var data = new CallGraphResponse(symbolId, graphNodes, traversal.TotalNodesFound, traversal.Truncated);
        var answer = AnswerGenerator.ForCallGraph(symbolId, direction, graphNodes.Count, clampedDepth, traversal.Truncated);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_card",
                $"Get full details for {rootCard.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = symbolId.Value })
        };

        var tokensSaved = TokenSavingsEstimator.ForSearch(graphNodes.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var semanticLevelGraph = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevelGraph);

        await _cache.SetAsync(cacheKey, envelope, ct);
        return Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(envelope);
    }

    // ─── GetTypeHierarchyAsync ────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>> GetTypeHierarchyAsync(
        RoutingContext routing,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Failure(baselineResult.Error);

        // Cache check
        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:hierarchy:{symbolId.Value}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<TypeHierarchyResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for hierarchy '{SymbolId}'", symbolId.Value);
            return Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(cached);
        }

        // Verify target symbol exists and is a type
        tc.StartPhase();
        var card = await _store.GetSymbolAsync(routing.RepoId, commitSha, symbolId, ct);
        if (card is null)
            return Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolId.Value));

        if (card.Kind is not (SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface
                              or SymbolKind.Enum or SymbolKind.Record or SymbolKind.Delegate))
            return Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument($"Symbol '{symbolId.Value}' is not a type (kind: {card.Kind})."));

        var relations = await _store.GetTypeRelationsAsync(routing.RepoId, commitSha, symbolId, ct);
        var derived = await _store.GetDerivedTypesAsync(routing.RepoId, commitSha, symbolId, ct);
        tc.EndDbQuery();

        var baseTypeRelation = relations.FirstOrDefault(r => r.RelationKind == TypeRelationKind.BaseType);
        var baseRef = baseTypeRelation is not null
            ? new TypeRef(baseTypeRelation.RelatedSymbolId, baseTypeRelation.DisplayName)
            : null;

        var interfaceRefs = relations
            .Where(r => r.RelationKind == TypeRelationKind.Interface)
            .Select(r => new TypeRef(r.RelatedSymbolId, r.DisplayName))
            .ToList();

        var derivedRefs = derived
            .Select(d => new TypeRef(d.TypeSymbolId, d.DisplayName))
            .ToList();

        var data = new TypeHierarchyResponse(symbolId, baseRef, interfaceRefs, derivedRefs);
        var answer = AnswerGenerator.ForTypeHierarchy(symbolId, baseRef, interfaceRefs.Count, derivedRefs.Count);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_card",
                $"Get full details for {card.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = symbolId.Value })
        };

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var semanticLevelHier = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, new Dictionary<string, LimitApplied>(),
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevelHier);

        await _cache.SetAsync(cacheKey, envelope, ct);
        return Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(envelope);
    }

    private async Task<List<CallGraphNode>> HydrateNodesAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<TraversedNode> traversedNodes,
        CancellationToken ct)
    {
        var cards = new Dictionary<SymbolId, SymbolCard>();
        foreach (var node in traversedNodes)
        {
            var card = await _store.GetSymbolAsync(repoId, commitSha, node.SymbolId, ct);
            if (card is not null)
                cards[node.SymbolId] = card;
        }

        return traversedNodes.Select(node =>
        {
            if (cards.TryGetValue(node.SymbolId, out var card))
                return new CallGraphNode(
                    node.SymbolId, card.FullyQualifiedName, card.Kind,
                    node.Depth, card.FilePath, card.SpanStart, node.ConnectedIds);
            // External symbol not in index — fallback display name
            return new CallGraphNode(
                node.SymbolId, node.SymbolId.Value, SymbolKind.Method,
                node.Depth, null, 0, node.ConnectedIds);
        }).ToList();
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task<SemanticLevel?> LoadSemanticLevelAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct)
    {
        try { return await _store.GetSemanticLevelAsync(repoId, commitSha, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static Result<CommitSha, CodeMapError> ResolveCommit(RoutingContext routing)
    {
        if (routing.BaselineCommitSha is { } sha)
            return Result<CommitSha, CodeMapError>.Success(sha);

        return Result<CommitSha, CodeMapError>.Failure(
            new CodeMapError(
                ErrorCodes.IndexNotAvailable,
                "No commit SHA in routing context — the MCP handler must call " +
                "IGitService.GetCurrentCommitAsync and pass it to RoutingContext. " +
                "If you are an agent, run index.ensure_baseline to verify the index exists."));
    }

    private async Task<Result<bool, CodeMapError>> EnsureBaselineAsync(
        RoutingContext routing, CommitSha commitSha, CancellationToken ct)
    {
        var exists = await _store.BaselineExistsAsync(routing.RepoId, commitSha, ct);
        if (!exists)
            return Result<bool, CodeMapError>.Failure(
                CodeMapError.IndexNotAvailable(routing.RepoId.Value, commitSha.Value));

        return Result<bool, CodeMapError>.Success(true);
    }

    private static string BuildSearchCacheKey(
        RepoId repoId, CommitSha commitSha, string query, SymbolSearchFilters? filters, int limit)
    {
        var canonical = $"{query}|kinds={string.Join(",", filters?.Kinds?.Select(k => k.ToString()) ?? [])}|ns={filters?.Namespace}|fp={filters?.FilePath}|proj={filters?.ProjectName}|limit={limit}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
        return $"{repoId.Value}:{commitSha.Value}:search:{hash}";
    }

    private static IReadOnlyList<EvidencePointer> BuildSymbolEvidence(RepoId repoId, SymbolCard card)
    {
        if (card.SpanStart < 1) return [];
        return
        [
            new EvidencePointer(
                repoId,
                card.FilePath,
                card.SpanStart,
                Math.Max(card.SpanStart, card.SpanEnd),
                card.SymbolId)
        ];
    }

    private static IReadOnlyList<NextAction> NextActionsForSearch(IReadOnlyList<SymbolSearchHit> hits)
    {
        return hits
            .Take(3)
            .Select(h => new NextAction(
                "symbols.get_card",
                $"Get full details for {h.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = h.SymbolId.Value }))
            .ToList();
    }

    private static IReadOnlyList<NextAction> NextActionsForCard(SymbolCard card)
    {
        return
        [
            new NextAction(
                "symbols.get_definition_span",
                $"View source code for {card.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = card.SymbolId.Value })
        ];
    }

    private static IReadOnlyList<NextAction> NextActionsForSpan() => [];

    // ─── GetSymbolByStableIdAsync ─────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolByStableIdAsync(
        RoutingContext routing,
        StableId stableId,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        if (stableId.IsEmpty)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument("stable_id must not be empty."));

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(baselineResult.Error);

        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:card:stable:{stableId.Value}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SymbolCard>>(cacheKey, ct);
        tc.EndCacheLookup();
        if (cached is not null)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(cached);

        tc.StartPhase();
        var card = await _store.GetSymbolByStableIdAsync(routing.RepoId, commitSha, stableId, ct);
        tc.EndDbQuery();

        if (card is null)
            return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", stableId.Value));

        var evidence = BuildSymbolEvidence(routing.RepoId, card);
        var answer = AnswerGenerator.ForCard(card);
        var nextActions = NextActionsForCard(card);

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var noLimits = _noLimits;
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            card, answer, evidence, nextActions,
            card.Confidence, timing, noLimits,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        await _cache.SetAsync(cacheKey, envelope, ct);
        return Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope);
    }

    // ─── ListEndpointsAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>> ListEndpointsAsync(
        RoutingContext routing,
        string? pathFilter,
        string? httpMethod,
        int limit,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Failure(baselineResult.Error);

        tc.StartPhase();
        var clampedLimit = limit > 0 ? limit : 50;
        var stored = await _store.GetFactsByKindAsync(
            routing.RepoId, commitSha, Core.Enums.FactKind.Route, clampedLimit + 1, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        var truncated = stored.Count > clampedLimit;
        var page = truncated ? stored.Take(clampedLimit).ToList() : (IReadOnlyList<StoredFact>)stored;

        var endpoints = BuildEndpoints(page, pathFilter, httpMethod);
        var data = new ListEndpointsResponse(endpoints, endpoints.Count, truncated);

        var answer = AnswerGenerator.ForEndpoints(endpoints.Count, pathFilter, httpMethod, truncated);
        IReadOnlyList<EvidencePointer> noEvidence = [];
        IReadOnlyList<NextAction> noActions = [];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var limitsApplied = new Dictionary<string, LimitApplied>
        {
            ["limit"] = new LimitApplied(Requested: clampedLimit, HardCap: clampedLimit),
        };
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, noEvidence, noActions,
            Core.Enums.Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        return Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Success(envelope);
    }

    private static IReadOnlyList<EndpointInfo> BuildEndpoints(
        IReadOnlyList<StoredFact> facts,
        string? pathFilter,
        string? httpMethod)
    {
        var result = new List<EndpointInfo>();
        foreach (var fact in facts)
        {
            // Parse "METHOD /path" from fact.Value
            var space = fact.Value.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0) continue;

            var method = fact.Value[..space];
            var path = fact.Value[(space + 1)..];

            if (!string.IsNullOrEmpty(httpMethod) &&
                !string.Equals(method, httpMethod, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(pathFilter) &&
                !path.StartsWith(pathFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new EndpointInfo(
                HttpMethod: method,
                RoutePath: path,
                HandlerSymbol: fact.SymbolId,
                FilePath: fact.FilePath,
                Line: fact.LineStart,
                Confidence: fact.Confidence));
        }
        return result;
    }

    // ─── ListConfigKeysAsync ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>> ListConfigKeysAsync(
        RoutingContext routing,
        string? keyFilter,
        int limit,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Failure(baselineResult.Error);

        tc.StartPhase();
        var clampedLimit = limit > 0 ? limit : 50;
        var stored = await _store.GetFactsByKindAsync(
            routing.RepoId, commitSha, Core.Enums.FactKind.Config, clampedLimit + 1, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        var truncated = stored.Count > clampedLimit;
        var page = truncated ? stored.Take(clampedLimit).ToList() : (IReadOnlyList<StoredFact>)stored;

        var keys = BuildConfigKeys(page, keyFilter);
        var data = new ListConfigKeysResponse(keys, keys.Count, truncated);

        var answer = AnswerGenerator.ForConfigKeys(keys.Count, keyFilter, truncated);
        IReadOnlyList<EvidencePointer> noEvidence = [];
        IReadOnlyList<NextAction> noActions = [];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var limitsApplied = new Dictionary<string, LimitApplied>
        {
            ["limit"] = new LimitApplied(Requested: clampedLimit, HardCap: clampedLimit),
        };
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, noEvidence, noActions,
            Core.Enums.Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        return Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Success(envelope);
    }

    private static IReadOnlyList<ConfigKeyInfo> BuildConfigKeys(
        IReadOnlyList<StoredFact> facts,
        string? keyFilter)
    {
        var result = new List<ConfigKeyInfo>();
        foreach (var fact in facts)
        {
            // Parse "key|usage_pattern" from fact.Value
            var pipe = fact.Value.IndexOf('|', StringComparison.Ordinal);
            string key, pattern;
            if (pipe >= 0)
            {
                key = fact.Value[..pipe];
                pattern = fact.Value[(pipe + 1)..];
            }
            else
            {
                key = fact.Value;
                pattern = "unknown";
            }

            if (!string.IsNullOrEmpty(keyFilter) &&
                !key.StartsWith(keyFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new ConfigKeyInfo(
                Key: key,
                UsedBySymbol: fact.SymbolId,
                FilePath: fact.FilePath,
                Line: fact.LineStart,
                UsagePattern: pattern,
                Confidence: fact.Confidence));
        }
        return result;
    }

    // ─── ListDbTablesAsync ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>> ListDbTablesAsync(
        RoutingContext routing,
        string? tableFilter,
        int limit,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Failure(baselineResult.Error);

        tc.StartPhase();
        var clampedLimit = limit > 0 ? limit : 50;
        // Fetch many facts to allow grouping by table name
        var fetchCap = Math.Max(clampedLimit * 10, 500);
        var stored = await _store.GetFactsByKindAsync(
            routing.RepoId, commitSha, Core.Enums.FactKind.DbTable, fetchCap, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        var tables = BuildDbTables(stored, tableFilter);
        var truncated = tables.Count > clampedLimit;
        var page = truncated ? tables.Take(clampedLimit).ToList() : tables;

        var data = new ListDbTablesResponse(page, page.Count, truncated);
        var answer = AnswerGenerator.ForDbTables(page.Count, tableFilter, truncated);
        IReadOnlyList<EvidencePointer> noEvidence = [];
        IReadOnlyList<NextAction> noActions = [];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();

        var limitsApplied = new Dictionary<string, LimitApplied>
        {
            ["limit"] = new LimitApplied(Requested: clampedLimit, HardCap: clampedLimit),
        };
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, noEvidence, noActions,
            Core.Enums.Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        return Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Success(envelope);
    }

    private static List<DbTableInfo> BuildDbTables(
        IReadOnlyList<StoredFact> facts,
        string? tableFilter)
    {
        // Group facts by parsed table name; collect all referencing SymbolIds per table
        var groups = new Dictionary<string, (string? Schema, List<SymbolId> ReferencedBy, Core.Enums.Confidence Confidence, bool IsDbSet)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var fact in facts)
        {
            // Parse "tableName|sourcePattern" — table may include schema prefix (e.g. "dbo.Orders")
            var pipe = fact.Value.IndexOf('|', StringComparison.Ordinal);
            var tablePart = pipe >= 0 ? fact.Value[..pipe] : fact.Value;
            var sourcePattern = pipe >= 0 ? fact.Value[(pipe + 1)..] : "";

            // Split schema.tableName
            string? schema = null;
            string tableName = tablePart;
            var dot = tablePart.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                schema = tablePart[..dot];
                tableName = tablePart[(dot + 1)..];
            }

            // Apply prefix filter
            if (!string.IsNullOrEmpty(tableFilter) &&
                !tableName.StartsWith(tableFilter, StringComparison.OrdinalIgnoreCase) &&
                !tablePart.StartsWith(tableFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = tablePart; // group key = full "schema.table" or "table"
            if (!groups.TryGetValue(key, out var group))
            {
                group = (schema, new List<SymbolId>(), fact.Confidence, sourcePattern.Contains("DbSet"));
                groups[key] = group;
            }

            if (!group.ReferencedBy.Contains(fact.SymbolId))
                group.ReferencedBy.Add(fact.SymbolId);

            // Worst confidence wins
            if (fact.Confidence > group.Confidence)
                groups[key] = group with { Confidence = fact.Confidence };
        }

        var result = new List<DbTableInfo>(groups.Count);
        foreach (var (fullKey, group) in groups)
        {
            var dot = fullKey.IndexOf('.', StringComparison.Ordinal);
            var tableName = dot >= 0 ? fullKey[(dot + 1)..] : fullKey;

            // EntitySymbol: use first ReferencedBy for DbSet-sourced facts
            SymbolId? entitySymbol = group.IsDbSet && group.ReferencedBy.Count > 0
                ? group.ReferencedBy[0]
                : null;

            result.Add(new DbTableInfo(
                TableName: tableName,
                Schema: group.Schema,
                EntitySymbol: entitySymbol,
                ReferencedBy: group.ReferencedBy,
                Confidence: group.Confidence));
        }
        return result;
    }

    // ─── TraceFeatureAsync ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>> TraceFeatureAsync(
        RoutingContext routing,
        SymbolId entryPoint,
        int depth = 3,
        int limit = 100,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Failure(baselineResult.Error);

        var clampedDepth = Math.Clamp(depth, 1, 6);
        var clampedLimit = Math.Clamp(limit, 1, 500);

        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:trace:{entryPoint.Value}:d={clampedDepth}:lim={clampedLimit}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<FeatureTraceResponse>>(cacheKey, ct);
        tc.EndCacheLookup();

        if (cached is not null)
        {
            _logger.LogDebug("Cache hit for trace_feature '{EntryPoint}'", entryPoint.Value);
            return Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Success(cached);
        }

        tc.StartPhase();
        var traceResult = await _featureTracer.TraceAsync(
            routing.RepoId, commitSha, entryPoint, clampedDepth, clampedLimit, ct: ct).ConfigureAwait(false);
        tc.EndDbQuery();

        if (traceResult.IsFailure)
            return Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Failure(traceResult.Error);

        var data = traceResult.Value;
        var answer = AnswerGenerator.ForFeatureTrace(data.EntryPointName, data.TotalNodesTraversed, data.Depth, data.Truncated);

        var tokensSaved = TokenSavingsEstimator.ForSearch(data.TotalNodesTraversed);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], [],
            Confidence.High, timing, new Dictionary<string, LimitApplied>(),
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        await _cache.SetAsync(cacheKey, envelope, ct);
        return Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Success(envelope);
    }

    // ─── SummarizeAsync ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>> SummarizeAsync(
        RoutingContext routing,
        string? repoPath = null,
        string[]? sectionFilter = null,
        int maxItemsPerSection = 50,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>.Failure(baselineResult.Error);

        tc.StartPhase();

        var solutionName = InferSolutionName(repoPath);
        var summary = await CodebaseSummarizer.SummarizeAsync(
            _store, routing.RepoId, commitSha,
            solutionName, sectionFilter, maxItemsPerSection, ct).ConfigureAwait(false);

        tc.EndDbQuery();

        var tokensSaved = TokenSavingsEstimator.ForCard() * Math.Max(summary.Stats.FactCount, 1);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();
        var semanticLevel = summary.Stats.SemanticLevel;
        var answer = $"Generated codebase summary for '{summary.SolutionName}': " +
                     $"{summary.Sections.Count} sections, {summary.Stats.FactCount} facts indexed.";

        var envelope = EnvelopeBuilder.Build(
            summary, answer, [], [],
            Confidence.High, timing, new Dictionary<string, LimitApplied>(),
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        return Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>.Success(envelope);
    }

    // ─── ExportAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<ExportResponse>, CodeMapError>> ExportAsync(
        RoutingContext routing,
        string detail = "standard",
        string format = "markdown",
        int maxTokens = 4000,
        string[]? sectionFilter = null,
        string? repoPath = null,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Failure(baselineResult.Error);

        tc.StartPhase();

        var solutionName = InferSolutionName(repoPath);
        var export = await CodebaseExporter.ExportAsync(
            _store, routing.RepoId, commitSha,
            solutionName, detail, format, maxTokens, sectionFilter, ct).ConfigureAwait(false);

        tc.EndDbQuery();

        var tokensSaved = TokenSavingsEstimator.ForCard() * Math.Max(export.Stats.SymbolCount, 1);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();
        var answer = $"Exported '{solutionName}' codebase ({detail} detail, {format} format, " +
                     $"~{export.EstimatedTokens} tokens{(export.Truncated ? ", truncated" : "")}).";

        var envelope = EnvelopeBuilder.Build(
            export, answer, [], [],
            Confidence.High, timing, new Dictionary<string, LimitApplied>(),
            commitSha, tokensSaved, costAvoided,
            semanticLevel: export.Stats.SemanticLevel);

        return Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Success(envelope);
    }

    // ─── DiffAsync ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<DiffResponse>, CodeMapError>> DiffAsync(
        RoutingContext routing,
        CommitSha fromCommit,
        CommitSha toCommit,
        IReadOnlyList<SymbolKind>? kinds = null,
        bool includeFacts = true,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();
        tc.StartPhase();

        var diff = await SemanticDiffer.DiffAsync(
            _store, routing.RepoId, fromCommit, toCommit, kinds, includeFacts, ct).ConfigureAwait(false);

        tc.EndDbQuery();

        var totalChanges = diff.SymbolChanges.Count + diff.FactChanges.Count;
        var tokensSaved  = TokenSavingsEstimator.ForCard() * Math.Max(totalChanges, 1);
        var costAvoided  = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();
        var answer = $"Diff {fromCommit.Value[..7]} \u2192 {toCommit.Value[..7]}: " +
                     $"{diff.Stats.SymbolsAdded} added, {diff.Stats.SymbolsRemoved} removed, " +
                     $"{diff.Stats.SymbolsRenamed} renamed.";

        var envelope = EnvelopeBuilder.Build(
            diff, answer, [], [],
            Confidence.High, timing, new Dictionary<string, LimitApplied>(),
            toCommit, tokensSaved, costAvoided);

        return Result<ResponseEnvelope<DiffResponse>, CodeMapError>.Success(envelope);
    }

    // ─── GetContextAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>> GetContextAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int calleeDepth = 1,
        int maxCallees = 10,
        bool includeCode = true,
        CancellationToken ct = default)
    {
        var tc = new TimingContext();

        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Failure(baselineResult.Error);

        tc.StartPhase();
        var collectResult = await ContextBuilder.CollectAsync(
            this, routing, symbolId,
            calleeDepth: Math.Clamp(calleeDepth, 0, 2),
            maxCallees: Math.Clamp(maxCallees, 0, 25),
            includeCode, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        if (collectResult.IsFailure)
            return Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Failure(collectResult.Error);

        var d = collectResult.Value;
        var markdown = ContextBuilder.RenderMarkdown(d.Primary.Card, d.Primary.SourceCode, d.Callees);
        var data = new SymbolContextResponse(d.Primary, d.Callees, d.TotalCalleesFound, markdown);

        var answer = AnswerGenerator.ForContext(
            d.Primary.Card.FullyQualifiedName, d.Callees.Count, d.TotalCalleesFound);
        var tokensSaved = TokenSavingsEstimator.ForContext(d.Callees.Count + 1);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        IReadOnlyList<EvidencePointer> evidence = d.Primary.Card.SpanStart >= 1
            ? [new(routing.RepoId, d.Primary.Card.FilePath, d.Primary.Card.SpanStart,
                   Math.Max(d.Primary.Card.SpanStart, d.Primary.Card.SpanEnd), d.Primary.Card.SymbolId)]
            : [];

        var timing = tc.Build();
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var noLimits = _noLimits;
        var envelope = EnvelopeBuilder.Build(
            data, answer, evidence, [],
            d.Primary.Card.Confidence, timing, noLimits,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        return Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Success(envelope);
    }

    // ─── SearchTextAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>> SearchTextAsync(
        RoutingContext routing,
        string pattern,
        string? filePathFilter,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        // 1. Compile regex (fail fast on bad pattern)
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Failure(
                CodeMapError.InvalidArgument($"Invalid regex pattern: {ex.Message}"));
        }

        var tc = new TimingContext();

        // 2. Resolve commit + ensure baseline
        var commitResult = ResolveCommit(routing);
        if (commitResult.IsFailure)
            return Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Failure(commitResult.Error);
        var commitSha = commitResult.Value;

        var baselineResult = await EnsureBaselineAsync(routing, commitSha, ct);
        if (baselineResult.IsFailure)
            return Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Failure(baselineResult.Error);

        // 3. Get repo root
        var repoRoot = await _store.GetRepoRootAsync(routing.RepoId, commitSha, ct);
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Failure(
                CodeMapError.IndexNotAvailable(routing.RepoId.Value, commitSha.Value));

        // 4. Clamp budget + check cache (after commit + baseline are confirmed valid)
        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var cap = clamped.MaxResults;
        var cacheKey = $"{routing.RepoId.Value}:{commitSha.Value}:searchtext:{pattern}:{filePathFilter ?? ""}:{cap}";
        tc.StartPhase();
        var cachedResult = await _cache.GetAsync<ResponseEnvelope<SearchTextResponse>>(cacheKey, ct);
        tc.EndCacheLookup();
        if (cachedResult is not null)
        {
            _logger.LogDebug("Cache hit for search_text '{Pattern}'", pattern);
            return Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Success(cachedResult);
        }

        // 5. Get indexed file list with stored content
        tc.StartPhase();
        var allFiles = await _store.GetAllFileContentsAsync(routing.RepoId, commitSha, ct) ?? [];
        tc.EndDbQuery();

        // 6. Apply file_path filter
        IReadOnlyList<(FilePath Path, string? Content)> filteredFiles = string.IsNullOrEmpty(filePathFilter)
            ? allFiles
            : allFiles.Where(f => f.Path.Value.StartsWith(filePathFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var matches = new List<TextMatch>();
        bool truncated = false;

        // 7. Scan files — use stored content when available; fall back to disk for old baselines
        tc.StartPhase();
        foreach (var (filePath, storedContent) in filteredFiles)
        {
            if (ct.IsCancellationRequested) break;

            string[] lines;
            if (storedContent is not null)
            {
                lines = storedContent.Split('\n');
                // Normalise: strip trailing \r so regex matches behave as on disk
                for (int j = 0; j < lines.Length; j++)
                    if (lines[j].EndsWith('\r')) lines[j] = lines[j][..^1];
            }
            else
            {
                var absolutePath = Path.Combine(repoRoot,
                    filePath.Value.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolutePath)) continue;
                try { lines = await File.ReadAllLinesAsync(absolutePath, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                bool matched;
                try { matched = regex.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { matched = false; }

                if (matched)
                {
                    matches.Add(new TextMatch(filePath, i + 1, lines[i].Trim()));
                    if (matches.Count > cap)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            if (truncated) break;
        }
        tc.EndRanking();

        // Remove the +1 probe entry
        if (truncated) matches.RemoveAt(matches.Count - 1);

        // 8. Build response
        var data = new SearchTextResponse(pattern, matches, filteredFiles.Count, truncated);
        var answer = AnswerGenerator.ForSearchText(data);
        IReadOnlyList<EvidencePointer> evidence = [];

        var tokensSaved = TokenSavingsEstimator.ForSearchText(filteredFiles.Count, matches.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var timing = tc.Build();
        var semanticLevel = await LoadSemanticLevelAsync(routing.RepoId, commitSha, ct);
        var envelope = EnvelopeBuilder.Build(
            data, answer, evidence, [],
            Confidence.High, timing, limitsApplied,
            commitSha, tokensSaved, costAvoided,
            semanticLevel: semanticLevel);

        await _cache.SetAsync(cacheKey, envelope, ct);
        return Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Success(envelope);
    }

    private static string InferSolutionName(string? repoRootPath)
    {
        if (string.IsNullOrEmpty(repoRootPath)) return "Unknown";
        var normalized = repoRootPath.TrimEnd('/', '\\');
        var sep = normalized.LastIndexOfAny(['/', '\\']);
        return sep >= 0 ? normalized[(sep + 1)..] : normalized;
    }
}
