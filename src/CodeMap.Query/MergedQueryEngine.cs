namespace CodeMap.Query;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Extensions.Logging;
using RefKind = CodeMap.Core.Enums.RefKind;

/// <summary>
/// Decorator around <see cref="IQueryEngine"/> that adds overlay merge for workspace-mode queries.
///
/// For committed-mode queries, delegates directly to the inner engine with zero overhead.
/// For workspace-mode queries, fetches overlay data from <see cref="IOverlayStore"/> and
/// merges it with baseline results using the overlay-wins strategy.
/// </summary>
public class MergedQueryEngine : IQueryEngine
{
    private readonly IQueryEngine _inner;
    private readonly IOverlayStore _overlayStore;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ICacheService _cache;
    private readonly ITokenSavingsTracker _tracker;
    private readonly ExcerptReader _excerptReader;
    private readonly GraphTraverser _graphTraverser;
    private readonly ILogger<MergedQueryEngine> _logger;

    private static readonly IReadOnlyDictionary<string, LimitApplied> _noLimits =
        new Dictionary<string, LimitApplied>(0);

    public MergedQueryEngine(
        IQueryEngine inner,
        IOverlayStore overlayStore,
        WorkspaceManager workspaceManager,
        ICacheService cache,
        ITokenSavingsTracker tracker,
        ExcerptReader excerptReader,
        GraphTraverser graphTraverser,
        ILogger<MergedQueryEngine> logger)
    {
        _inner = inner;
        _overlayStore = overlayStore;
        _workspaceManager = workspaceManager;
        _cache = cache;
        _tracker = tracker;
        _excerptReader = excerptReader;
        _graphTraverser = graphTraverser;
        _logger = logger;
    }

    // ─── SearchSymbolsAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> overlay symbols appear first (recently modified files have priority).
    /// Baseline symbols are included only when their <c>symbol_id</c> is not in the deleted set
    /// AND their <c>file_path</c> is not in the overlay-reindexed file set (file-authoritative merge).
    /// Deduplication is by file path — no FQN dedup applied.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>> SearchSymbolsAsync(
        RoutingContext routing,
        string query,
        SymbolSearchFilters? filters,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.SearchSymbolsAsync(routing, query, filters, budgets, ct)
                               .ConfigureAwait(false);

        // === Workspace mode ===
        var tc = new TimingContext();

        // 1. Validate workspace
        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<SymbolSearchResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        // 2. Resolve budgets
        if (string.IsNullOrWhiteSpace(query))
            return Fail<ResponseEnvelope<SymbolSearchResponse>>(
                CodeMapError.InvalidArgument("Query string is required."));

        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var maxResults = clamped.MaxResults;

        // 3. Check workspace-scoped cache
        var cacheKey = BuildWorkspaceSearchKey(
            routing.RepoId, ws.BaselineCommitSha, RequiredWorkspaceId(routing),
            ws.CurrentRevision, query, filters, maxResults);
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SymbolSearchResponse>>(cacheKey, ct)
                                  .ConfigureAwait(false);
        tc.EndCacheLookup();
        if (cached is not null)
            return Ok(cached);

        // 4. Fetch overlay metadata
        tc.StartPhase();
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var overlayFiles = await _overlayStore.GetOverlayFilePathsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var overlayHits = await _overlayStore.SearchOverlaySymbolsAsync(
            routing.RepoId, RequiredWorkspaceId(routing), query, filters, maxResults, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        // 5. Query baseline (request extra to compensate for later filtering)
        var committedRouting = new RoutingContext(
            repoId: routing.RepoId,
            baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.SearchSymbolsAsync(
            committedRouting, query, filters,
            new BudgetLimits(maxResults: maxResults * 2), ct).ConfigureAwait(false);
        var baselineHits = baselineResult.IsSuccess
            ? baselineResult.Value.Data.Hits
            : (IReadOnlyList<SymbolSearchHit>)[];

        // 6. Merge
        var merged = MergeHelpers.MergeSearchResults(
            baselineHits, overlayHits, deletedIds, overlayFiles, maxResults);

        // 7. Build envelope
        var data = new SymbolSearchResponse(merged.Hits, merged.TotalCount, merged.Truncated);
        var answer = AnswerGenerator.ForSearch(merged.Hits, query, merged.Truncated);
        var nextActions = merged.Hits.Take(3)
            .Select(h => new NextAction(
                "symbols.get_card",
                $"Get full details for {h.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = h.SymbolId.Value }))
            .ToList();

        var tokensSaved = TokenSavingsEstimator.ForSearch(merged.Hits.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        var costPerModel = TokenSavingsEstimator.EstimateCostPerModel(tokensSaved);
        _tracker.RecordSaving(tokensSaved, costPerModel);

        var baselineLevel = baselineResult.IsSuccess ? baselineResult.Value.Meta.SemanticLevel : null;
        var overlayLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var mergedLevel = MergeSemanticLevels(baselineLevel, overlayLevel);

        var timing = tc.Build();
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, limitsApplied,
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: mergedLevel);

        // 8. Cache and return
        await _cache.SetAsync(cacheKey, envelope, ct).ConfigureAwait(false);
        return Ok(envelope);
    }

    // ─── GetSymbolCardAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> checks overlay first; if found, uses overlay card.
    /// If not in overlay, falls back to baseline card.
    /// <see cref="Core.Models.SymbolCard.Facts"/> are then replaced with overlay facts when present
    /// (overlay-wins — overlay facts take full precedence over baseline facts for the same symbol).
    /// Deleted symbols return <c>NOT_FOUND</c>.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolCardAsync(
        RoutingContext routing,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.GetSymbolCardAsync(routing, symbolId, ct).ConfigureAwait(false);

        // === Workspace mode ===
        var tc = new TimingContext();

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<SymbolCard>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var cacheKey = BuildWorkspaceCardKey(
            routing.RepoId, ws.BaselineCommitSha, RequiredWorkspaceId(routing),
            ws.CurrentRevision, symbolId);
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SymbolCard>>(cacheKey, ct).ConfigureAwait(false);
        tc.EndCacheLookup();
        if (cached is not null)
            return Ok(cached);

        // Check if deleted
        tc.StartPhase();
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        if (deletedIds.Contains(symbolId))
            return Fail<ResponseEnvelope<SymbolCard>>(
                CodeMapError.NotFound("Symbol", $"{symbolId.Value} (deleted in workspace {RequiredWorkspaceId(routing).Value})"));

        // Overlay-first merge
        var overlayCard = await _overlayStore.GetOverlaySymbolAsync(
            routing.RepoId, RequiredWorkspaceId(routing), symbolId, ct).ConfigureAwait(false);

        SymbolCard card;
        if (overlayCard is not null)
        {
            card = overlayCard;
        }
        else
        {
            var committedRouting = new RoutingContext(
                repoId: routing.RepoId,
                baselineCommitSha: ws.BaselineCommitSha);
            var baselineResult = await _inner.GetSymbolCardAsync(committedRouting, symbolId, ct).ConfigureAwait(false);
            if (baselineResult.IsFailure)
                return baselineResult;
            card = baselineResult.Value.Data;
        }

        // Hydrate Facts: overlay facts take precedence; fall back to baseline facts already in card
        var overlayFacts = await _overlayStore.GetOverlayFactsForSymbolAsync(
            routing.RepoId, RequiredWorkspaceId(routing), card.SymbolId, ct).ConfigureAwait(false);
        if (overlayFacts?.Count > 0)
            card = card with { Facts = overlayFacts.Select(f => new Core.Models.Fact(f.Kind, f.Value)).ToList() };
        tc.EndDbQuery();

        // Build envelope
        var answer = AnswerGenerator.ForCard(card);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_definition_span",
                $"View source code for {card.FullyQualifiedName}",
                new Dictionary<string, object> { ["symbol_id"] = card.SymbolId.Value })
        };
        var evidence = card.SpanStart >= 1
            ? new List<EvidencePointer>
            {
                new(routing.RepoId, card.FilePath, card.SpanStart,
                    Math.Max(card.SpanStart, card.SpanEnd), card.SymbolId)
            }
            : (IReadOnlyList<EvidencePointer>)[];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        var costPerModel = TokenSavingsEstimator.EstimateCostPerModel(tokensSaved);
        _tracker.RecordSaving(tokensSaved, costPerModel);

        var cardOverlayLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        var timing = tc.Build();
        var noLimits = _noLimits;
        var envelope = EnvelopeBuilder.Build(
            card, answer, evidence, nextActions,
            Confidence.High, timing, noLimits,
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: cardOverlayLevel);

        await _cache.SetAsync(cacheKey, envelope, ct).ConfigureAwait(false);
        return Ok(envelope);
    }

    // ─── GetSpanAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>No semantic overlay merge.</b> File content is read from the working copy on disk.
    /// In Workspace mode, the working copy already reflects the agent's edits.
    /// In Ephemeral mode, virtual file content is checked first; if the file is not virtual,
    /// falls through to the Workspace disk-read path.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SpanResponse>, CodeMapError>> GetSpanAsync(
        RoutingContext routing,
        FilePath filePath,
        int startLine,
        int endLine,
        int contextLines,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        // Ephemeral mode: check virtual files first, fall back to workspace disk read
        if (routing.Consistency == ConsistencyMode.Ephemeral)
        {
            var ctxStart = Math.Max(1, startLine - contextLines);
            var ctxEnd = endLine + contextLines;

            var virtualSpan = VirtualFileResolver.BuildSpan(filePath, routing.VirtualFiles, ctxStart, ctxEnd);
            if (virtualSpan is not null)
            {
                var data = new SpanResponse(virtualSpan.FilePath, virtualSpan.StartLine, virtualSpan.EndLine,
                                               virtualSpan.TotalFileLines, virtualSpan.Content, virtualSpan.Truncated);
                var answer = AnswerGenerator.ForSpan(data);
                var timing = new TimingBreakdown(0);
                var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
                var commitSha = ws?.BaselineCommitSha ?? routing.BaselineCommitSha!.Value;

                var tokensSaved = TokenSavingsEstimator.ForSpan(data.TotalFileLines, data.EndLine - data.StartLine + 1);
                var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
                _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

                var ephVirtualLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
                var envelope = EnvelopeBuilder.Build(
                    data, answer, [], [],
                    Confidence.High, timing, new Dictionary<string, LimitApplied>(),
                    commitSha, tokensSaved, costAvoided,
                    routing.WorkspaceId, ws?.CurrentRevision ?? 0,
                    semanticLevel: ephVirtualLevel);
                return Ok(envelope);
            }

            // File not virtual — fall through to workspace mode disk read
            var workspaceRouting = new RoutingContext(
                repoId: routing.RepoId,
                workspaceId: routing.WorkspaceId,
                consistency: ConsistencyMode.Workspace,
                baselineCommitSha: routing.BaselineCommitSha);
            return await GetSpanAsync(workspaceRouting, filePath, startLine, endLine, contextLines, budgets, ct)
                             .ConfigureAwait(false);
        }

        // File content is read from disk (working copy). In workspace mode, the working copy
        // IS the edited version. No overlay merge needed — delegate with committed routing.
        var committedRouting = routing.Consistency == ConsistencyMode.Workspace
            ? new RoutingContext(repoId: routing.RepoId, baselineCommitSha: routing.BaselineCommitSha)
            : routing;

        return await _inner.GetSpanAsync(committedRouting, filePath, startLine, endLine, contextLines, budgets, ct)
                           .ConfigureAwait(false);
    }

    // ─── GetDefinitionSpanAsync ───────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> overlay-first card lookup to get the symbol's file/line coordinates,
    /// then reads file content from disk (working copy) via the committed routing path.
    /// Ephemeral mode checks virtual files first for the span content; falls through to workspace
    /// disk read if the file is not in the virtual file set.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SpanResponse>, CodeMapError>> GetDefinitionSpanAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int maxLines,
        int contextLines,
        CancellationToken ct = default)
    {
        // Ephemeral mode: resolve symbol coordinates then check virtual files
        if (routing.Consistency == ConsistencyMode.Ephemeral)
        {
            // Get workspace info for card lookup
            var wsForEphemeral = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
            if (wsForEphemeral is not null)
            {
                // Get card (overlay first, then baseline)
                var overlayCardForEphemeral = await _overlayStore.GetOverlaySymbolAsync(
                    routing.RepoId, RequiredWorkspaceId(routing), symbolId, ct).ConfigureAwait(false);

                SymbolCard? ephemeralCard = overlayCardForEphemeral;
                if (ephemeralCard is null)
                {
                    var committedR = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: wsForEphemeral.BaselineCommitSha);
                    var cardRes = await _inner.GetSymbolCardAsync(committedR, symbolId, ct).ConfigureAwait(false);
                    if (cardRes.IsSuccess) ephemeralCard = cardRes.Value.Data;
                }

                if (ephemeralCard is not null)
                {
                    var ephSpanEnd = Math.Min(ephemeralCard.SpanEnd, ephemeralCard.SpanStart + maxLines - 1);
                    var ephCtxStart = Math.Max(1, ephemeralCard.SpanStart - contextLines);
                    var ephCtxEnd = ephSpanEnd + contextLines;

                    var virtualSpan = VirtualFileResolver.BuildSpan(ephemeralCard.FilePath, routing.VirtualFiles, ephCtxStart, ephCtxEnd);
                    if (virtualSpan is not null)
                    {
                        var ephData = new SpanResponse(virtualSpan.FilePath, virtualSpan.StartLine, virtualSpan.EndLine,
                                                          virtualSpan.TotalFileLines, virtualSpan.Content, virtualSpan.Truncated);
                        var ephAnswer = AnswerGenerator.ForDefinitionSpan(ephemeralCard, ephData);
                        var ephTiming = new TimingBreakdown(0);
                        var ephTokens = TokenSavingsEstimator.ForSpan(ephData.TotalFileLines, ephData.EndLine - ephData.StartLine + 1);
                        var ephCost = TokenSavingsEstimator.EstimateCostAvoided(ephTokens);
                        _tracker.RecordSaving(ephTokens, TokenSavingsEstimator.EstimateCostPerModel(ephTokens));
                        var ephEvidence = new List<EvidencePointer>
                        {
                            new(routing.RepoId, ephemeralCard.FilePath, ephemeralCard.SpanStart,
                                Math.Max(ephemeralCard.SpanStart, ephemeralCard.SpanEnd), ephemeralCard.SymbolId)
                        };
                        var ephDefLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
                        var ephEnvelope = EnvelopeBuilder.Build(
                            ephData, ephAnswer, ephEvidence, [],
                            Confidence.High, ephTiming, new Dictionary<string, LimitApplied>(),
                            wsForEphemeral.BaselineCommitSha, ephTokens, ephCost,
                            routing.WorkspaceId, wsForEphemeral.CurrentRevision,
                            semanticLevel: ephDefLevel);
                        return Ok(ephEnvelope);
                    }
                }
            }

            // Fall through to workspace mode if no virtual content available
            var workspaceRoutingForDefSpan = new RoutingContext(
                repoId: routing.RepoId,
                workspaceId: routing.WorkspaceId,
                consistency: ConsistencyMode.Workspace,
                baselineCommitSha: routing.BaselineCommitSha);
            return await GetDefinitionSpanAsync(workspaceRoutingForDefSpan, symbolId, maxLines, contextLines, ct)
                             .ConfigureAwait(false);
        }

        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.GetDefinitionSpanAsync(routing, symbolId, maxLines, contextLines, ct)
                               .ConfigureAwait(false);

        // === Workspace mode ===
        var tc = new TimingContext();

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<SpanResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var cacheKey = BuildWorkspaceDefSpanKey(
            routing.RepoId, ws.BaselineCommitSha, RequiredWorkspaceId(routing),
            ws.CurrentRevision, symbolId, maxLines, contextLines);
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<SpanResponse>>(cacheKey, ct).ConfigureAwait(false);
        tc.EndCacheLookup();
        if (cached is not null)
            return Ok(cached);

        // Check deleted
        tc.StartPhase();
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        if (deletedIds.Contains(symbolId))
            return Fail<ResponseEnvelope<SpanResponse>>(
                CodeMapError.NotFound("Symbol", $"{symbolId.Value} (deleted in workspace {RequiredWorkspaceId(routing).Value})"));

        // Overlay-first symbol merge
        var overlayCard = await _overlayStore.GetOverlaySymbolAsync(
            routing.RepoId, RequiredWorkspaceId(routing), symbolId, ct).ConfigureAwait(false);

        SymbolCard card;
        if (overlayCard is not null)
        {
            card = overlayCard;
        }
        else
        {
            var committedRouting2 = new RoutingContext(
                repoId: routing.RepoId,
                baselineCommitSha: ws.BaselineCommitSha);
            var baselineResult = await _inner.GetSymbolCardAsync(committedRouting2, symbolId, ct).ConfigureAwait(false);
            if (baselineResult.IsFailure)
                return Fail<ResponseEnvelope<SpanResponse>>(baselineResult.Error);
            card = baselineResult.Value.Data;
        }
        tc.EndDbQuery();

        // Apply span limits and context
        var spanEnd = Math.Min(card.SpanEnd, card.SpanStart + maxLines - 1);
        var ctxStart = Math.Max(1, card.SpanStart - contextLines);
        var ctxEnd = spanEnd + contextLines;

        // Read from working copy (disk) using committed routing
        var spanRouting = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha);
        var spanResult = await _inner.GetSpanAsync(spanRouting, card.FilePath, ctxStart, ctxEnd, 0, null, ct)
                                      .ConfigureAwait(false);
        if (spanResult.IsFailure)
            return spanResult;

        // Build envelope with the span data but using definition-span answer
        var spanData = spanResult.Value.Data;
        var answer = AnswerGenerator.ForDefinitionSpan(card, spanData);
        var nextActions = new List<NextAction>();
        var evidence = new List<EvidencePointer>
        {
            new(routing.RepoId, card.FilePath, card.SpanStart,
                Math.Max(card.SpanStart, card.SpanEnd), card.SymbolId)
        };

        var tokensSaved = TokenSavingsEstimator.ForSpan(spanData.TotalFileLines, spanData.EndLine - spanData.StartLine + 1);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        var costPerModel = TokenSavingsEstimator.EstimateCostPerModel(tokensSaved);
        _tracker.RecordSaving(tokensSaved, costPerModel);

        var wsDefLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var timing = tc.Build();
        var noLimits = _noLimits;
        var envelope = EnvelopeBuilder.Build(
            spanData, answer, evidence, nextActions,
            Confidence.High, timing, noLimits,
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: wsDefLevel);

        await _cache.SetAsync(cacheKey, envelope, ct).ConfigureAwait(false);
        return Ok(envelope);
    }

    // ─── FindReferencesAsync ──────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge — overlay file-authoritative:</b> overlay refs appear first.
    /// Baseline refs are excluded for any file that was reindexed in the overlay
    /// (the overlay fully supersedes the baseline for those files).
    /// Excerpts are added for overlay refs (baseline refs already carry excerpts from the inner engine).
    /// </remarks>
    public async Task<Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>> FindReferencesAsync(
        RoutingContext routing,
        SymbolId symbolId,
        RefKind? kind,
        BudgetLimits? budgets,
        CancellationToken ct = default,
        ResolutionState? resolutionState = null)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.FindReferencesAsync(routing, symbolId, kind, budgets, ct, resolutionState)
                               .ConfigureAwait(false);

        // === Workspace mode ===
        var tc = new TimingContext();

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<FindRefsResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        // Resolve budgets
        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var maxRefs = clamped.MaxReferences;

        // Check workspace-scoped cache
        var cacheKey = $"{routing.RepoId.Value}:{ws.BaselineCommitSha.Value}:ws:{RequiredWorkspaceId(routing).Value}:rev:{ws.CurrentRevision}:refs:{symbolId.Value}:k={kind}:lim={maxRefs}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<FindRefsResponse>>(cacheKey, ct).ConfigureAwait(false);
        tc.EndCacheLookup();
        if (cached is not null)
            return Ok(cached);

        // Check if symbol is deleted
        tc.StartPhase();
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        if (deletedIds.Contains(symbolId))
            return Fail<ResponseEnvelope<FindRefsResponse>>(
                CodeMapError.NotFound("Symbol", $"{symbolId.Value} (deleted in workspace {RequiredWorkspaceId(routing).Value})"));

        // Get overlay file paths
        var overlayFiles = await _overlayStore.GetOverlayFilePathsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        // Query overlay refs
        var overlayStoredRefs = await _overlayStore.GetOverlayReferencesAsync(
            routing.RepoId, RequiredWorkspaceId(routing), symbolId, kind, maxRefs, ct).ConfigureAwait(false);
        tc.EndDbQuery();

        // Query baseline refs via inner engine (already includes excerpts)
        var committedRouting = new RoutingContext(
            repoId: routing.RepoId,
            baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.FindReferencesAsync(
            committedRouting, symbolId, kind,
            new BudgetLimits(maxReferences: maxRefs * 2), ct, resolutionState).ConfigureAwait(false);
        var baselineRefs = baselineResult.IsSuccess
            ? baselineResult.Value.Data.References
            : (IReadOnlyList<ClassifiedReference>)[];

        // Merge: overlay refs + baseline refs excluding reindexed files
        var filteredBaseline = baselineRefs
            .Where(r => !overlayFiles.Contains(r.FilePath))
            .ToList();

        var overlayClassified = overlayStoredRefs
            .Select(r => new ClassifiedReference(r.Kind, r.FromSymbol, r.FilePath, r.LineStart, r.LineEnd, null,
                r.ResolutionState, r.ToName, r.ToContainerHint))
            .ToList();

        var merged = overlayClassified.Concat(filteredBaseline).Take(maxRefs + 1).ToList();
        var truncated = merged.Count > maxRefs;
        var refs = merged.Take(maxRefs).ToList();

        // Add excerpts for overlay refs (baseline refs already have excerpts)
        var refsWithExcerpts = new List<ClassifiedReference>(refs.Count);
        foreach (var r in refs)
        {
            if (r.Excerpt is null)
            {
                var excerpt = await _excerptReader.ReadLineAsync(
                    routing.RepoId, ws.BaselineCommitSha, r.FilePath, r.LineStart, ct).ConfigureAwait(false);
                refsWithExcerpts.Add(r with { Excerpt = excerpt });
            }
            else
            {
                refsWithExcerpts.Add(r);
            }
        }

        // Build envelope
        var data = new FindRefsResponse(symbolId, refsWithExcerpts, truncated ? maxRefs + 1 : refsWithExcerpts.Count, truncated);
        var answer = AnswerGenerator.ForFindRefs(symbolId, refsWithExcerpts.Count, kind, truncated);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_card",
                $"Get symbol details",
                new Dictionary<string, object> { ["symbol_id"] = symbolId.Value })
        };

        var tokensSaved = TokenSavingsEstimator.ForSearch(refsWithExcerpts.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var refsBaselineLevel = baselineResult.IsSuccess ? baselineResult.Value.Meta.SemanticLevel : null;
        var refsOverlayLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var refsMergedLevel = MergeSemanticLevels(refsBaselineLevel, refsOverlayLevel);

        var timing = tc.Build();
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, limitsApplied,
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: refsMergedLevel);

        await _cache.SetAsync(cacheKey, envelope, ct).ConfigureAwait(false);
        return Ok(envelope);
    }

    // ─── GetCallersAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> BFS traversal using overlay refs merged with baseline refs.
    /// Baseline refs from overlay-reindexed files are excluded (file-authoritative merge).
    /// Deleted symbols are filtered from the result at each BFS level.
    /// Node hydration uses overlay-first card lookup.
    /// </remarks>
    public Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> GetCallersAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return _inner.GetCallersAsync(routing, symbolId, depth, limitPerLevel, budgets, ct);

        return TraverseWorkspaceGraphAsync(routing, symbolId, depth, limitPerLevel, budgets, "callers",
            expandNode: async (sid, ws, deletedIds, overlayFiles, clampedLimit, token) =>
            {
                var overlayRefs = await _overlayStore.GetOverlayReferencesAsync(
                    routing.RepoId, RequiredWorkspaceId(routing), sid, null, clampedLimit * 2, token).ConfigureAwait(false);
                var baselineRefs = await _inner.FindReferencesAsync(
                    new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha),
                    sid, null, new BudgetLimits(maxReferences: clampedLimit * 2), token).ConfigureAwait(false);
                var baselineList = baselineRefs.IsSuccess ? baselineRefs.Value.Data.References : [];

                return overlayRefs
                    .Where(r => r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate)
                    .Select(r => r.FromSymbol)
                    .Concat(baselineList
                        .Where(r => (r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate)
                                    && !overlayFiles.Contains(r.FilePath))
                        .Select(r => r.FromSymbol))
                    .Where(id => !deletedIds.Contains(id))
                    .Distinct()
                    .Take(clampedLimit)
                    .ToList<SymbolId>();
            }, ct);
    }

    // ─── GetCalleesAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> BFS traversal using overlay outgoing refs merged with baseline callees.
    /// When overlay files exist, conservative merge: overlay callees take priority.
    /// Deleted symbols are filtered at each BFS level. Node hydration uses overlay-first card lookup.
    /// Shares the <c>TraverseWorkspaceGraphAsync</c> helper with <c>GetCallersAsync</c>.
    /// </remarks>
    public Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> GetCalleesAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return _inner.GetCalleesAsync(routing, symbolId, depth, limitPerLevel, budgets, ct);

        return TraverseWorkspaceGraphAsync(routing, symbolId, depth, limitPerLevel, budgets, "callees",
            expandNode: async (sid, ws, deletedIds, overlayFiles, clampedLimit, token) =>
            {
                var overlayRefs = await _overlayStore.GetOutgoingOverlayReferencesAsync(
                    routing.RepoId, RequiredWorkspaceId(routing), sid, null, clampedLimit * 2, token).ConfigureAwait(false);
                var baselineRefs = await _inner.GetCalleesAsync(
                    new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha),
                    sid, depth: 1, limitPerLevel: clampedLimit * 2,
                    new BudgetLimits(maxReferences: clampedLimit * 2), token).ConfigureAwait(false);
                var baselineNodes = baselineRefs.IsSuccess ? baselineRefs.Value.Data.Nodes : [];

                // Collect baseline callee IDs from depth-1 nodes (direct callees of sid)
                var baselineCalleeIds = baselineNodes
                    .Where(n => n.Depth == 1)
                    .Select(n => n.SymbolId);

                return overlayRefs
                    .Where(r => r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate)
                    .Select(r => r.ToSymbol)
                    .Concat(baselineCalleeIds
                        .Where(id => !overlayFiles.Any()))  // simple: skip if any overlay files (conservative merge)
                    .Where(id => !deletedIds.Contains(id))
                    .Distinct()
                    .Take(clampedLimit)
                    .ToList<SymbolId>();
            }, ct);
    }

    private async Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> TraverseWorkspaceGraphAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        string direction,
        Func<SymbolId, WorkspaceInfo, IReadOnlySet<SymbolId>, IReadOnlySet<FilePath>, int, CancellationToken, Task<IReadOnlyList<SymbolId>>> expandNode,
        CancellationToken ct)
    {
        var tc = new TimingContext();

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<CallGraphResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var (clamped, limitsApplied) = (budgets ?? BudgetLimits.Defaults).ClampToHardCaps();
        var clampedDepth = Math.Min(depth, clamped.MaxDepth);
        var clampedLimit = Math.Min(limitPerLevel, clamped.MaxReferences);

        var cacheKey = $"{routing.RepoId.Value}:{ws.BaselineCommitSha.Value}:ws:{RequiredWorkspaceId(routing).Value}:rev:{ws.CurrentRevision}:{direction}:{symbolId.Value}:d={clampedDepth}:lim={clampedLimit}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<CallGraphResponse>>(cacheKey, ct).ConfigureAwait(false);
        tc.EndCacheLookup();
        if (cached is not null)
            return Ok(cached);

        tc.StartPhase();
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        if (deletedIds.Contains(symbolId))
            return Fail<ResponseEnvelope<CallGraphResponse>>(
                CodeMapError.NotFound("Symbol", $"{symbolId.Value} (deleted in workspace {RequiredWorkspaceId(routing).Value})"));

        var overlayFiles = await _overlayStore.GetOverlayFilePathsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        var traversal = await _graphTraverser.TraverseAsync(
            symbolId,
            (sid, token) => expandNode(sid, ws, deletedIds, overlayFiles, clampedLimit + 1, token),
            clampedDepth, clampedLimit, ct).ConfigureAwait(false);

        // Hydrate nodes using overlay-first card lookup
        var graphNodes = new List<CallGraphNode>(traversal.Nodes.Count);
        foreach (var node in traversal.Nodes)
        {
            var overlayCard = await _overlayStore.GetOverlaySymbolAsync(
                routing.RepoId, RequiredWorkspaceId(routing), node.SymbolId, ct).ConfigureAwait(false);
            if (overlayCard is not null)
            {
                graphNodes.Add(new CallGraphNode(
                    node.SymbolId, overlayCard.FullyQualifiedName, overlayCard.Kind,
                    node.Depth, overlayCard.FilePath, overlayCard.SpanStart, node.ConnectedIds));
                continue;
            }

            var committedRouting = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha);
            var baselineCard = await _inner.GetSymbolCardAsync(committedRouting, node.SymbolId, ct).ConfigureAwait(false);
            if (baselineCard.IsSuccess)
            {
                var c = baselineCard.Value.Data;
                graphNodes.Add(new CallGraphNode(
                    node.SymbolId, c.FullyQualifiedName, c.Kind,
                    node.Depth, c.FilePath, c.SpanStart, node.ConnectedIds));
            }
            else
            {
                graphNodes.Add(new CallGraphNode(
                    node.SymbolId, node.SymbolId.Value, SymbolKind.Method,
                    node.Depth, null, 0, node.ConnectedIds));
            }
        }
        tc.EndDbQuery();

        var data = new CallGraphResponse(symbolId, graphNodes, traversal.TotalNodesFound, traversal.Truncated);
        var answer = AnswerGenerator.ForCallGraph(symbolId, direction, graphNodes.Count, clampedDepth, traversal.Truncated);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_card",
                $"Get symbol details",
                new Dictionary<string, object> { ["symbol_id"] = symbolId.Value })
        };

        var tokensSaved = TokenSavingsEstimator.ForSearch(graphNodes.Count);
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var graphOverlayLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        var timing = tc.Build();
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, limitsApplied,
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: graphOverlayLevel);

        await _cache.SetAsync(cacheKey, envelope, ct).ConfigureAwait(false);
        return Ok(envelope);
    }

    // ─── GetTypeHierarchyAsync ────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> if the type's file was reindexed in the overlay, uses overlay type
    /// relations (base type + interfaces). Otherwise uses baseline relations (no change in that file).
    /// Derived types: overlay-first (overlay supersedes same-id baseline entries), then baseline
    /// minus deleted symbols and overlay-covered types.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>> GetTypeHierarchyAsync(
        RoutingContext routing,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.GetTypeHierarchyAsync(routing, symbolId, ct).ConfigureAwait(false);

        // === Workspace mode ===
        var tc = new TimingContext();

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<TypeHierarchyResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        // Cache check
        var cacheKey = $"{routing.RepoId.Value}:{ws.BaselineCommitSha.Value}:ws:{RequiredWorkspaceId(routing).Value}:rev:{ws.CurrentRevision}:hierarchy:{symbolId.Value}";
        tc.StartPhase();
        var cached = await _cache.GetAsync<ResponseEnvelope<TypeHierarchyResponse>>(cacheKey, ct).ConfigureAwait(false);
        tc.EndCacheLookup();
        if (cached is not null)
            return Ok(cached);

        // Check if the type itself is deleted
        tc.StartPhase();
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        if (deletedIds.Contains(symbolId))
            return Fail<ResponseEnvelope<TypeHierarchyResponse>>(
                CodeMapError.NotFound("Symbol", symbolId.Value));

        var overlayFiles = await _overlayStore.GetOverlayFilePathsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        // Get baseline hierarchy (validates symbol existence + kind)
        var committedRouting = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.GetTypeHierarchyAsync(committedRouting, symbolId, ct).ConfigureAwait(false);
        if (baselineResult.IsFailure)
            return Fail<ResponseEnvelope<TypeHierarchyResponse>>(baselineResult.Error);

        var baselineHierarchy = baselineResult.Value.Data;

        // Get overlay type relations and derived types
        var overlayRelations = await _overlayStore.GetOverlayTypeRelationsAsync(
            routing.RepoId, RequiredWorkspaceId(routing), symbolId, ct).ConfigureAwait(false);
        var overlayDerived = await _overlayStore.GetOverlayDerivedTypesAsync(
            routing.RepoId, RequiredWorkspaceId(routing), symbolId, ct).ConfigureAwait(false);

        // Resolve the card's file path (overlay first, then baseline)
        var overlayCard = await _overlayStore.GetOverlaySymbolAsync(routing.RepoId, RequiredWorkspaceId(routing), symbolId, ct).ConfigureAwait(false);
        var cardFilePath = overlayCard?.FilePath;
        if (cardFilePath is null)
        {
            var cardResult = await _inner.GetSymbolCardAsync(committedRouting, symbolId, ct).ConfigureAwait(false);
            if (cardResult.IsSuccess) cardFilePath = cardResult.Value.Data.FilePath;
        }
        tc.EndDbQuery();

        // If the type's file was reindexed in the overlay, use overlay relations
        // Otherwise use baseline relations
        TypeRef? baseRef;
        List<TypeRef> interfaceRefs;
        if (cardFilePath.HasValue && overlayFiles.Contains(cardFilePath.Value))
        {
            var baseTypeRelation = overlayRelations.FirstOrDefault(r => r.RelationKind == TypeRelationKind.BaseType);
            baseRef = baseTypeRelation is not null
                ? new TypeRef(baseTypeRelation.RelatedSymbolId, baseTypeRelation.DisplayName)
                : null;
            interfaceRefs = overlayRelations
                .Where(r => r.RelationKind == TypeRelationKind.Interface)
                .Select(r => new TypeRef(r.RelatedSymbolId, r.DisplayName))
                .ToList();
        }
        else
        {
            baseRef = baselineHierarchy.BaseType;
            interfaceRefs = [.. baselineHierarchy.Interfaces];
        }

        // Merge derived types: overlay-first, then baseline (excluding overlay-covered and deleted)
        var overlayDerivedTypeIds = overlayDerived.Select(d => d.TypeSymbolId).ToHashSet();
        var filteredBaselineDerived = baselineHierarchy.DerivedTypes
            .Where(d => !overlayDerivedTypeIds.Contains(d.SymbolId))
            .Where(d => !deletedIds.Contains(d.SymbolId))
            .ToList();

        // overlayDerived DisplayName = derived type's name (via JOIN in OverlayStore)
        var overlayDerivedRefs = overlayDerived
            .Select(d => new TypeRef(d.TypeSymbolId, d.DisplayName))
            .ToList();

        var derivedRefs = overlayDerivedRefs.Concat(filteredBaselineDerived).ToList();

        var cardFqn = overlayCard?.FullyQualifiedName ?? symbolId.Value;

        var data = new TypeHierarchyResponse(symbolId, baseRef, interfaceRefs, derivedRefs);
        var answer = AnswerGenerator.ForTypeHierarchy(symbolId, baseRef, interfaceRefs.Count, derivedRefs.Count);
        var nextActions = new List<NextAction>
        {
            new("symbols.get_card",
                $"Get full details for {cardFqn}",
                new Dictionary<string, object> { ["symbol_id"] = symbolId.Value })
        };

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);
        _tracker.RecordSaving(tokensSaved, TokenSavingsEstimator.EstimateCostPerModel(tokensSaved));

        var hierBaselineLevel = baselineResult.Value.Meta.SemanticLevel;
        var hierOverlayLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var hierMergedLevel = MergeSemanticLevels(hierBaselineLevel, hierOverlayLevel);

        var timing = tc.Build();
        var envelope = EnvelopeBuilder.Build(
            data, answer, [], nextActions,
            Confidence.High, timing, new Dictionary<string, LimitApplied>(),
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: hierMergedLevel);

        await _cache.SetAsync(cacheKey, envelope, ct).ConfigureAwait(false);
        return Ok(envelope);
    }

    // ─── Cache key builders ───────────────────────────────────────────────────

    private static string BuildWorkspaceSearchKey(
        RepoId repoId, CommitSha commitSha, WorkspaceId workspaceId,
        int revision, string query, SymbolSearchFilters? filters, int limit)
    {
        var canonical = $"{query}|kinds={string.Join(",", filters?.Kinds?.Select(k => k.ToString()) ?? [])}|ns={filters?.Namespace}|fp={filters?.FilePath}|proj={filters?.ProjectName}|limit={limit}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
        return $"{repoId.Value}:{commitSha.Value}:ws:{workspaceId.Value}:rev:{revision}:search:{hash}";
    }

    private static string BuildWorkspaceCardKey(
        RepoId repoId, CommitSha commitSha, WorkspaceId workspaceId,
        int revision, SymbolId symbolId) =>
        $"{repoId.Value}:{commitSha.Value}:ws:{workspaceId.Value}:rev:{revision}:card:{symbolId.Value}";

    private static string BuildWorkspaceDefSpanKey(
        RepoId repoId, CommitSha commitSha, WorkspaceId workspaceId,
        int revision, SymbolId symbolId, int maxLines, int contextLines) =>
        $"{repoId.Value}:{commitSha.Value}:ws:{workspaceId.Value}:rev:{revision}:defspan:{symbolId.Value}:{maxLines}:{contextLines}";

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts Ephemeral routing to Workspace routing for semantic queries.
    /// Virtual files do not affect semantic queries (search, cards, refs, graph, hierarchy) —
    /// Ephemeral mode is identical to Workspace mode for all non-span queries.
    /// </summary>
    private async Task<SemanticLevel?> GetOverlaySemanticLevelAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct)
    {
        try { return await _overlayStore.GetOverlaySemanticLevelAsync(repoId, workspaceId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Returns the worst-quality SemanticLevel (Full > Partial > SyntaxOnly).</summary>
    private static SemanticLevel? MergeSemanticLevels(SemanticLevel? baseline, SemanticLevel? overlay)
    {
        if (!baseline.HasValue && !overlay.HasValue) return null;
        if (!baseline.HasValue) return overlay;
        if (!overlay.HasValue) return baseline;
        return (SemanticLevel)Math.Max((int)baseline.Value, (int)overlay.Value);
    }

    private static RoutingContext NormalizeEphemeralToWorkspace(RoutingContext routing)
    {
        if (routing.Consistency != ConsistencyMode.Ephemeral) return routing;
        return new RoutingContext(
            repoId: routing.RepoId,
            workspaceId: routing.WorkspaceId,
            consistency: ConsistencyMode.Workspace,
            baselineCommitSha: routing.BaselineCommitSha);
    }

    // ─── GetSymbolByStableIdAsync ─────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge:</b> tries overlay store by stable_id first; if found, validates it is
    /// not in the deleted set, then delegates to <see cref="GetSymbolCardAsync"/> for full card +
    /// facts hydration. If not in overlay, falls back to baseline stable_id lookup.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolByStableIdAsync(
        RoutingContext routing,
        StableId stableId,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.GetSymbolByStableIdAsync(routing, stableId, ct).ConfigureAwait(false);

        // === Workspace mode: overlay-first by stable_id ===
        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<SymbolCard>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        // Try overlay
        var overlayCard = await _overlayStore.GetSymbolByStableIdAsync(
            routing.RepoId, RequiredWorkspaceId(routing), stableId, ct).ConfigureAwait(false);

        if (overlayCard is not null)
        {
            // Check it's not in the deleted set
            var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(
                routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
            if (deletedIds.Contains(overlayCard.SymbolId))
                return Fail<ResponseEnvelope<SymbolCard>>(
                    CodeMapError.NotFound("Symbol", stableId.Value));

            // Build via regular card lookup (reuses cache + envelope logic)
            var committedR2 = new RoutingContext(routing.RepoId, routing.WorkspaceId, ConsistencyMode.Workspace, ws.BaselineCommitSha);
            return await GetSymbolCardAsync(committedR2, overlayCard.SymbolId, ct).ConfigureAwait(false);
        }

        // Fall back to baseline
        var committedRouting = new RoutingContext(
            repoId: routing.RepoId,
            baselineCommitSha: ws.BaselineCommitSha);
        return await _inner.GetSymbolByStableIdAsync(committedRouting, stableId, ct).ConfigureAwait(false);
    }

    // ─── ListEndpointsAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge — overlay-wins-by-file:</b> overlay endpoint facts appear first.
    /// Baseline endpoints from overlay-reindexed files are excluded (file-authoritative merge).
    /// Short-circuits to baseline result when no overlay facts exist for this workspace.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>> ListEndpointsAsync(
        RoutingContext routing,
        string? pathFilter,
        string? httpMethod,
        int limit,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.ListEndpointsAsync(routing, pathFilter, httpMethod, limit, ct).ConfigureAwait(false);

        // === Workspace mode: merge overlay facts with baseline facts ===
        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<ListEndpointsResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        // Baseline results (committed routing)
        var committedRouting = new RoutingContext(
            repoId: routing.RepoId,
            baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.ListEndpointsAsync(
            committedRouting, pathFilter, httpMethod, limit, ct).ConfigureAwait(false);
        if (baselineResult.IsFailure)
            return Fail<ResponseEnvelope<ListEndpointsResponse>>(baselineResult.Error);

        // Overlay facts for same kind
        var overlayFacts = await _overlayStore.GetOverlayFactsByKindAsync(
            routing.RepoId, RequiredWorkspaceId(routing),
            Core.Enums.FactKind.Route, limit, ct).ConfigureAwait(false);

        if (overlayFacts.Count == 0)
            return Ok(baselineResult.Value);

        // Overlay files — prefer overlay endpoints for reindexed files
        var overlayFiles = await _overlayStore.GetOverlayFilePathsAsync(
            routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        var baselineEndpoints = baselineResult.Value.Data.Endpoints
            .Where(e => !overlayFiles.Contains(e.FilePath))
            .ToList();

        var overlayEndpoints = BuildOverlayEndpoints(overlayFacts, pathFilter, httpMethod);

        var merged = overlayEndpoints.Concat(baselineEndpoints)
            .Take(limit)
            .ToList();

        var truncated = merged.Count >= limit;
        var data = new ListEndpointsResponse(merged, merged.Count, truncated);

        var revision = ws.CurrentRevision;
        var answer = AnswerGenerator.ForEndpoints(merged.Count, pathFilter, httpMethod, truncated);
        IReadOnlyList<EvidencePointer> noEvidence = [];
        IReadOnlyList<NextAction> noActions = [];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);

        var envelope = EnvelopeBuilder.Build(
            data, answer, noEvidence, noActions,
            Core.Enums.Confidence.High,
            new TimingBreakdown(0),
            new Dictionary<string, LimitApplied>(),
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            workspaceId: RequiredWorkspaceId(routing),
            overlayRevision: revision);

        return Ok(envelope);
    }

    private static IReadOnlyList<EndpointInfo> BuildOverlayEndpoints(
        IReadOnlyList<StoredFact> facts,
        string? pathFilter,
        string? httpMethod)
    {
        var result = new List<EndpointInfo>();
        foreach (var fact in facts)
        {
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
            result.Add(new EndpointInfo(method, path, fact.SymbolId, fact.FilePath,
                fact.LineStart, fact.Confidence));
        }
        return result;
    }

    // ─── ListConfigKeysAsync ──────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge — overlay-wins-by-file:</b> overlay config key facts appear first.
    /// Baseline config keys from overlay-reindexed files are excluded (file-authoritative merge).
    /// Short-circuits to baseline result when no overlay facts exist for this workspace.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>> ListConfigKeysAsync(
        RoutingContext routing,
        string? keyFilter,
        int limit,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.ListConfigKeysAsync(routing, keyFilter, limit, ct).ConfigureAwait(false);

        // === Workspace mode: merge overlay config facts with baseline facts ===
        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<ListConfigKeysResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var committedRouting = new RoutingContext(
            repoId: routing.RepoId,
            baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.ListConfigKeysAsync(
            committedRouting, keyFilter, limit, ct).ConfigureAwait(false);
        if (baselineResult.IsFailure)
            return Fail<ResponseEnvelope<ListConfigKeysResponse>>(baselineResult.Error);

        var overlayFacts = await _overlayStore.GetOverlayFactsByKindAsync(
            routing.RepoId, RequiredWorkspaceId(routing),
            Core.Enums.FactKind.Config, limit, ct).ConfigureAwait(false);

        if (overlayFacts.Count == 0)
            return Ok(baselineResult.Value);

        var overlayFiles = await _overlayStore.GetOverlayFilePathsAsync(
            routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);

        var baselineKeys = baselineResult.Value.Data.Keys
            .Where(k => !overlayFiles.Contains(k.FilePath))
            .ToList();

        var overlayKeys = BuildOverlayConfigKeys(overlayFacts, keyFilter);

        var merged = overlayKeys.Concat(baselineKeys)
            .Take(limit)
            .ToList();

        var truncated = merged.Count >= limit;
        var data = new ListConfigKeysResponse(merged, merged.Count, truncated);

        var revision = ws.CurrentRevision;
        var answer = AnswerGenerator.ForConfigKeys(merged.Count, keyFilter, truncated);
        IReadOnlyList<EvidencePointer> noEvidence = [];
        IReadOnlyList<NextAction> noActions = [];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);

        var envelope = EnvelopeBuilder.Build(
            data, answer, noEvidence, noActions,
            Core.Enums.Confidence.High,
            new TimingBreakdown(0),
            new Dictionary<string, LimitApplied>(),
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            workspaceId: RequiredWorkspaceId(routing),
            overlayRevision: revision);

        return Ok(envelope);
    }

    private static IReadOnlyList<ConfigKeyInfo> BuildOverlayConfigKeys(
        IReadOnlyList<StoredFact> facts,
        string? keyFilter)
    {
        var result = new List<ConfigKeyInfo>();
        foreach (var fact in facts)
        {
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

            result.Add(new ConfigKeyInfo(key, fact.SymbolId, fact.FilePath,
                fact.LineStart, pattern, fact.Confidence));
        }
        return result;
    }

    // ─── ListDbTablesAsync ────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge — overlay-wins-by-table-name:</b> overlay DB table facts appear first.
    /// Baseline tables whose name (case-insensitive) matches an overlay table are excluded.
    /// Uses table-name key (not file path) because DB tables are aggregated views — the same
    /// table can be referenced from multiple files.
    /// Short-circuits to baseline result when no overlay facts exist for this workspace.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>> ListDbTablesAsync(
        RoutingContext routing,
        string? tableFilter,
        int limit,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.ListDbTablesAsync(routing, tableFilter, limit, ct).ConfigureAwait(false);

        // === Workspace mode: merge overlay DB table facts with baseline facts ===
        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<ListDbTablesResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var committedRouting = new RoutingContext(
            repoId: routing.RepoId,
            baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.ListDbTablesAsync(
            committedRouting, tableFilter, limit, ct).ConfigureAwait(false);
        if (baselineResult.IsFailure)
            return Fail<ResponseEnvelope<ListDbTablesResponse>>(baselineResult.Error);

        var overlayFacts = await _overlayStore.GetOverlayFactsByKindAsync(
            routing.RepoId, RequiredWorkspaceId(routing),
            Core.Enums.FactKind.DbTable, limit, ct).ConfigureAwait(false);

        if (overlayFacts.Count == 0)
            return Ok(baselineResult.Value);

        // Build overlay tables
        var overlayTables = BuildOverlayDbTables(overlayFacts, tableFilter);

        // Build set of table names covered by overlay (overlay-wins by table name)
        var overlayTableNames = overlayTables
            .Select(t => t.TableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter baseline: exclude tables whose name is superseded by overlay
        var baselineTables = baselineResult.Value.Data.Tables
            .Where(t => !overlayTableNames.Contains(t.TableName))
            .ToList();

        var merged = overlayTables.Concat(baselineTables).Take(limit).ToList();
        var truncated = merged.Count >= limit;
        var data = new ListDbTablesResponse(merged, merged.Count, truncated);

        var revision = ws.CurrentRevision;
        var answer = AnswerGenerator.ForDbTables(merged.Count, tableFilter, truncated);
        IReadOnlyList<EvidencePointer> noEvidence = [];
        IReadOnlyList<NextAction> noActions = [];

        var tokensSaved = TokenSavingsEstimator.ForCard();
        var costAvoided = TokenSavingsEstimator.EstimateCostAvoided(tokensSaved);

        var envelope = EnvelopeBuilder.Build(
            data, answer, noEvidence, noActions,
            Core.Enums.Confidence.High,
            new TimingBreakdown(0),
            new Dictionary<string, LimitApplied>(),
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            workspaceId: RequiredWorkspaceId(routing),
            overlayRevision: revision);

        return Ok(envelope);
    }

    private static IReadOnlyList<DbTableInfo> BuildOverlayDbTables(
        IReadOnlyList<StoredFact> facts,
        string? tableFilter)
    {
        var groups = new Dictionary<string, (string? Schema, List<SymbolId> ReferencedBy, Core.Enums.Confidence Confidence, bool IsDbSet)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var fact in facts)
        {
            var pipe = fact.Value.IndexOf('|', StringComparison.Ordinal);
            var tablePart = pipe >= 0 ? fact.Value[..pipe] : fact.Value;
            var sourcePattern = pipe >= 0 ? fact.Value[(pipe + 1)..] : "";

            string? schema = null;
            string tableName = tablePart;
            var dot = tablePart.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                schema = tablePart[..dot];
                tableName = tablePart[(dot + 1)..];
            }

            if (!string.IsNullOrEmpty(tableFilter) &&
                !tableName.StartsWith(tableFilter, StringComparison.OrdinalIgnoreCase) &&
                !tablePart.StartsWith(tableFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!groups.TryGetValue(tablePart, out var group))
            {
                group = (schema, new List<SymbolId>(), fact.Confidence, sourcePattern.Contains("DbSet"));
                groups[tablePart] = group;
            }

            if (!group.ReferencedBy.Contains(fact.SymbolId))
                group.ReferencedBy.Add(fact.SymbolId);
        }

        var result = new List<DbTableInfo>(groups.Count);
        foreach (var (fullKey, group) in groups)
        {
            var dot = fullKey.IndexOf('.', StringComparison.Ordinal);
            var tableName = dot >= 0 ? fullKey[(dot + 1)..] : fullKey;

            SymbolId? entitySymbol = group.IsDbSet && group.ReferencedBy.Count > 0
                ? group.ReferencedBy[0]
                : null;

            result.Add(new DbTableInfo(tableName, group.Schema, entitySymbol,
                group.ReferencedBy, group.Confidence));
        }
        return result;
    }

    // ─── TraceFeatureAsync ────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace merge — overlay fact post-processing:</b> the BFS traversal runs against the
    /// committed baseline (no overlay-aware traversal). After the baseline tree is built, each node's
    /// <see cref="Core.Models.TraceAnnotation"/> list is replaced with overlay facts when present
    /// (overlay-wins per node). Tree structure (shape, depth, children) is not modified.
    /// Entry point deleted in workspace → <c>NOT_FOUND</c>.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>> TraceFeatureAsync(
        RoutingContext routing,
        SymbolId entryPoint,
        int depth = 3,
        int limit = 100,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.TraceFeatureAsync(routing, entryPoint, depth, limit, ct).ConfigureAwait(false);

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<FeatureTraceResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        // Check if entry point is deleted in overlay
        var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        if (deletedIds.Contains(entryPoint))
            return Fail<ResponseEnvelope<FeatureTraceResponse>>(
                CodeMapError.NotFound("Symbol", $"{entryPoint.Value} (deleted in workspace {RequiredWorkspaceId(routing).Value})"));

        // Get baseline trace using committed routing
        var committedRouting = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha);
        var baselineResult = await _inner.TraceFeatureAsync(committedRouting, entryPoint, depth, limit, ct).ConfigureAwait(false);
        if (baselineResult.IsFailure)
            return Fail<ResponseEnvelope<FeatureTraceResponse>>(baselineResult.Error);

        // Enrich tree nodes with overlay facts (overlay-wins per node)
        var enrichedNodes = new List<TraceNode>();
        foreach (var node in baselineResult.Value.Data.Nodes)
            enrichedNodes.Add(await EnrichTraceNodeWithOverlayFacts(node, routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false));

        var enrichedData = baselineResult.Value.Data with { Nodes = enrichedNodes };
        var enrichedEnvelope = baselineResult.Value with { Data = enrichedData };

        return Ok(enrichedEnvelope);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Workspace mode delegates to the inner engine using the workspace's baseline commit.
    /// Overlay facts are not individually addressable by FactKind without N+1 queries,
    /// so the summary always reflects the committed baseline index.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>> SummarizeAsync(
        RoutingContext routing,
        string? repoPath = null,
        string[]? sectionFilter = null,
        int maxItemsPerSection = 50,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.SummarizeAsync(routing, repoPath, sectionFilter, maxItemsPerSection, ct).ConfigureAwait(false);

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<SummarizeResponse>>(CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var committedRouting = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha);
        return await _inner.SummarizeAsync(committedRouting, repoPath, sectionFilter, maxItemsPerSection, ct).ConfigureAwait(false);
    }

    // ─── ExportAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Export always reflects the committed baseline index.
    /// Workspace overlay symbols are not included in the export sections.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<ExportResponse>, CodeMapError>> ExportAsync(
        RoutingContext routing,
        string detail = "standard",
        string format = "markdown",
        int maxTokens = 4000,
        string[]? sectionFilter = null,
        string? repoPath = null,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.ExportAsync(routing, detail, format, maxTokens, sectionFilter, repoPath, ct).ConfigureAwait(false);

        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<ExportResponse>>(CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var committedRouting = new RoutingContext(repoId: routing.RepoId, baselineCommitSha: ws.BaselineCommitSha);
        return await _inner.ExportAsync(committedRouting, detail, format, maxTokens, sectionFilter, repoPath, ct).ConfigureAwait(false);
    }

    // ─── DiffAsync ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Diff always compares two committed baselines — no overlay involvement.
    /// Only <see cref="RoutingContext.RepoId"/> is read from routing; both commit SHAs
    /// are explicit parameters. Delegates directly to the inner engine.
    /// </remarks>
    public Task<Result<ResponseEnvelope<DiffResponse>, CodeMapError>> DiffAsync(
        RoutingContext routing,
        CommitSha fromCommit,
        CommitSha toCommit,
        IReadOnlyList<SymbolKind>? kinds = null,
        bool includeFacts = true,
        CancellationToken ct = default)
        => _inner.DiffAsync(routing, fromCommit, toCommit, kinds, includeFacts, ct);

    // ─── GetContextAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Committed mode:</b> delegates to inner engine.
    /// <b>Workspace mode:</b> calls workspace-aware GetSymbolCardAsync / GetCalleesAsync /
    /// GetDefinitionSpanAsync (all on this instance), so overlay cards and disk-read code
    /// are included in the response. Modified callee code from working copy is returned.
    /// </remarks>
    public async Task<Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>> GetContextAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int calleeDepth = 1,
        int maxCallees = 10,
        bool includeCode = true,
        CancellationToken ct = default)
    {
        routing = NormalizeEphemeralToWorkspace(routing);
        if (routing.Consistency != ConsistencyMode.Workspace)
            return await _inner.GetContextAsync(routing, symbolId, calleeDepth, maxCallees, includeCode, ct).ConfigureAwait(false);

        // === Workspace mode ===
        var ws = _workspaceManager.GetWorkspaceInfo(routing.RepoId, RequiredWorkspaceId(routing));
        if (ws is null)
            return Fail<ResponseEnvelope<SymbolContextResponse>>(
                CodeMapError.NotFound("Workspace", RequiredWorkspaceId(routing).Value));

        var collectResult = await ContextBuilder.CollectAsync(
            this, routing, symbolId,
            calleeDepth: Math.Clamp(calleeDepth, 0, 2),
            maxCallees: Math.Clamp(maxCallees, 0, 25),
            includeCode, ct).ConfigureAwait(false);

        if (collectResult.IsFailure)
            return Fail<ResponseEnvelope<SymbolContextResponse>>(collectResult.Error);

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

        var overlayLevel = await GetOverlaySemanticLevelAsync(routing.RepoId, RequiredWorkspaceId(routing), ct).ConfigureAwait(false);
        var timing = new TimingBreakdown(0);
        var noLimits = _noLimits;
        var envelope = EnvelopeBuilder.Build(
            data, answer, evidence, [],
            d.Primary.Card.Confidence, timing, noLimits,
            ws.BaselineCommitSha, tokensSaved, costAvoided,
            routing.WorkspaceId, ws.CurrentRevision,
            semanticLevel: overlayLevel);

        return Ok(envelope);
    }

    private async Task<TraceNode> EnrichTraceNodeWithOverlayFacts(
        TraceNode node,
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct)
    {
        var overlayFacts = await _overlayStore.GetOverlayFactsForSymbolAsync(repoId, workspaceId, node.SymbolId, ct).ConfigureAwait(false);
        var annotations = overlayFacts?.Count > 0
            ? overlayFacts.Select(f => new TraceAnnotation(
                f.Kind.ToString(),
                FeatureTracer.ParseDisplayValue(f.Value) ?? f.Value,
                f.Confidence)).ToList<TraceAnnotation>()
            : node.Annotations;

        var enrichedChildren = new List<TraceNode>();
        foreach (var child in node.Children)
            enrichedChildren.Add(await EnrichTraceNodeWithOverlayFacts(child, repoId, workspaceId, ct).ConfigureAwait(false));

        return node with { Annotations = annotations, Children = enrichedChildren };
    }

    // ─── SearchTextAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Workspace/Ephemeral merge:</b> file content is read from disk (working copy).
    /// The baseline file inventory is used to enumerate files; new files not yet indexed
    /// will not be searched. Delegates to inner engine with committed routing.
    /// </remarks>
    public Task<Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>> SearchTextAsync(
        RoutingContext routing,
        string pattern,
        string? filePathFilter,
        BudgetLimits? budgets,
        CancellationToken ct = default)
    {
        // File content is read from disk regardless of mode.
        // Use committed routing so inner engine resolves the baseline's file list.
        var committedRouting = routing.Consistency == ConsistencyMode.Committed
            ? routing
            : new RoutingContext(repoId: routing.RepoId, baselineCommitSha: routing.BaselineCommitSha);

        return _inner.SearchTextAsync(committedRouting, pattern, filePathFilter, budgets, ct);
    }

    private static Result<T, CodeMapError> Ok<T>(T value) =>
        Result<T, CodeMapError>.Success(value);

    private static Result<T, CodeMapError> Fail<T>(CodeMapError error) =>
        Result<T, CodeMapError>.Failure(error);

    /// <summary>
    /// Returns the required WorkspaceId for workspace/ephemeral routing contexts.
    /// Throws <see cref="InvalidOperationException"/> with a descriptive message
    /// if WorkspaceId is absent — indicates a routing validation bug upstream.
    /// </summary>
    private static WorkspaceId RequiredWorkspaceId(RoutingContext routing) =>
        routing.WorkspaceId
        ?? throw new InvalidOperationException(
            $"WorkspaceId is required for {routing.Consistency} routing but was null.");
}
