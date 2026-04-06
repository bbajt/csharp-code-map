namespace CodeMap.Storage.Engine;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// ISymbolStore adapter for the v2 custom storage engine.
/// Phase 3: CreateBaselineAsync + BaselineExistsAsync.
/// Phase 4: All read-only query methods implemented.
/// Registered in DI when CODEMAP_ENGINE=custom.
/// </summary>
public sealed class CustomSymbolStore : ISymbolStore, IDisposable
{
    private readonly EngineBaselineBuilder _builder;
    private readonly string _storeBaseDir;
    private readonly Dictionary<string, (EngineBaselineReader Reader, EngineMergedReader Merged)> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EngineOverlay> _overlays = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();
    private bool _disposed;

    public CustomSymbolStore(string storeBaseDir)
    {
        _storeBaseDir = storeBaseDir;
        _builder = new EngineBaselineBuilder(storeBaseDir);
    }

    // ── Baseline lifecycle ───────────────────────────────────────────────────

    public async Task CreateBaselineAsync(RepoId repoId, CommitSha commitSha, CompilationResult data, string repoRootPath = "", CancellationToken ct = default)
    {
        var input = new BaselineBuildInput(
            CommitSha: commitSha.Value,
            RepoRootPath: repoRootPath,
            Symbols: data.Symbols,
            Files: data.Files,
            References: data.References,
            Facts: data.Facts ?? [],
            TypeRelations: data.TypeRelations ?? [],
            ProjectDiagnostics: data.Stats.ProjectDiagnostics);

        // Builder places baselines at <storeDir>/baselines/<commitSha>/
        // We scope storeDir by repoId so final path = <baseDir>/<repoId>/baselines/<commitSha>/
        var result = await new EngineBaselineBuilder(RepoStoreDir(repoId.Value)).BuildAsync(input, ct);
        if (!result.Success)
            throw new StorageWriteException($"Baseline build failed: {result.ErrorMessage}");

        // Invalidate cache for this commit
        var cacheKey = CacheKey(repoId.Value, commitSha.Value);
        lock (_cacheLock)
        {
            if (_cache.Remove(cacheKey, out var old))
                old.Reader.Dispose();
        }
    }

    public Task<bool> BaselineExistsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var baselineDir = BaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var exists = File.Exists(manifestPath) && ManifestWriter.Read(manifestPath) != null;
        return Task.FromResult(exists);
    }

    // ── Symbol queries ───────────────────────────────────────────────────────

    public Task<SymbolCard?> GetSymbolAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        SymbolRecord? rec;

        // sym_ prefix → StableId lookup
        if (symbolId.Value.StartsWith("sym_", StringComparison.Ordinal))
            rec = merged.GetSymbolByStableId(symbolId.Value);
        else
            rec = merged.GetSymbolByFqn(symbolId.Value);

        if (rec == null) return Task.FromResult<SymbolCard?>(null);
        return Task.FromResult<SymbolCard?>(RecordToCoreMappings.ToSymbolCard(rec.Value, reader));
    }

    public Task<SymbolCard?> GetSymbolByStableIdAsync(RepoId repoId, CommitSha commitSha, StableId stableId, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var rec = merged.GetSymbolByStableId(stableId.Value);
        if (rec == null) return Task.FromResult<SymbolCard?>(null);
        return Task.FromResult<SymbolCard?>(RecordToCoreMappings.ToSymbolCard(rec.Value, reader));
    }

    public Task<IReadOnlyList<SymbolSearchHit>> SearchSymbolsAsync(RepoId repoId, CommitSha commitSha, string query, SymbolSearchFilters? filters, int limit, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var filter = new SymbolSearchFilter(
            Kind: filters?.Kinds?.Count == 1 ? RecordMappers.MapSymbolKind(filters.Kinds[0]) : null,
            NamespacePrefix: filters?.Namespace,
            Limit: limit);

        var results = merged.SearchSymbols(query, filter);
        var hits = new List<SymbolSearchHit>(results.Length);
        foreach (var r in results)
            hits.Add(RecordToCoreMappings.ToSearchHit(r.Symbol, reader, r.Score));

        return Task.FromResult<IReadOnlyList<SymbolSearchHit>>(hits);
    }

    public Task<IReadOnlyList<SymbolSearchHit>> GetSymbolsByKindsAsync(RepoId repoId, CommitSha commitSha, IReadOnlyList<SymbolKind>? kinds, int limit, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var kindFilter = kinds?.Count == 1 ? RecordMappers.MapSymbolKind(kinds[0]) : (short?)null;
        var symbols = merged.EnumerateSymbols(kindFilter);

        var hits = new List<SymbolSearchHit>();
        foreach (var sym in symbols)
        {
            if (kinds != null && kinds.Count > 1)
            {
                var symKind = RecordToCoreMappings.ReverseSymbolKind(sym.Kind);
                if (!kinds.Contains(symKind)) continue;
            }
            hits.Add(RecordToCoreMappings.ToSearchHit(sym, reader, 0));
            if (hits.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<SymbolSearchHit>>(hits);
    }

    public Task<IReadOnlyList<SymbolCard>> GetSymbolsByFileAsync(RepoId repoId, CommitSha commitSha, FilePath filePath, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var records = merged.GetSymbolsByFile(filePath.Value);
        var cards = new List<SymbolCard>(records.Count);
        foreach (var r in records)
            cards.Add(RecordToCoreMappings.ToSymbolCard(r, reader));
        return Task.FromResult<IReadOnlyList<SymbolCard>>(cards);
    }

    // ── Reference queries ────────────────────────────────────────────────────

    public Task<IReadOnlyList<StoredReference>> GetReferencesAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, RefKind? kind, int limit, CancellationToken ct = default, ResolutionState? resolutionState = null)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredReference>>([]);

        var filter = new EdgeFilter(
            EdgeKind: kind.HasValue ? RecordMappers.MapEdgeKind(kind.Value) : null,
            ResolvedOnly: resolutionState == ResolutionState.Resolved);

        var edges = merged.GetIncomingEdges(intId, filter);
        var refs = new List<StoredReference>(Math.Min(edges.Count, limit));
        foreach (var e in edges)
        {
            if (resolutionState == ResolutionState.Unresolved && e.ResolutionState != 1) continue;
            refs.Add(RecordToCoreMappings.ToStoredReference(e, reader));
            if (refs.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<StoredReference>>(refs);
    }

    public Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingReferencesAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, RefKind? kind, int limit, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredOutgoingReference>>([]);

        var filter = new EdgeFilter(EdgeKind: kind.HasValue ? RecordMappers.MapEdgeKind(kind.Value) : null);
        var edges = merged.GetOutgoingEdges(intId, filter);
        var refs = new List<StoredOutgoingReference>(Math.Min(edges.Count, limit));
        foreach (var e in edges)
        {
            refs.Add(RecordToCoreMappings.ToOutgoingReference(e, reader));
            if (refs.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<StoredOutgoingReference>>(refs);
    }

    // ── Type hierarchy ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<StoredTypeRelation>> GetTypeRelationsAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]);

        // Outgoing Inherits(4) + Implements(5) edges = base types
        var edges = merged.GetOutgoingEdges(intId);
        var relations = new List<StoredTypeRelation>();
        foreach (var e in edges)
        {
            if (e.EdgeKind is not (4 or 5)) continue;

            string targetFqn;
            string targetDisplay;
            if (e.ToSymbolIntId > 0 && e.ToSymbolIntId <= reader.SymbolCount)
            {
                // Target is in the symbols table (local type)
                ref readonly var target = ref reader.GetSymbolByIntId(e.ToSymbolIntId);
                targetFqn = reader.ResolveString(target.FqnStringId);
                targetDisplay = reader.ResolveString(target.DisplayNameStringId);
            }
            else if (e.ToNameStringId > 0)
            {
                // Target is external (framework/DLL type) — stored as doc-comment FQN
                var storedName = reader.ResolveString(e.ToNameStringId);
                targetFqn = storedName;
                // Extract display name from FQN: "T:System.Windows.Forms.Form" → "Form"
                var lastDot = storedName.LastIndexOf('.');
                targetDisplay = lastDot >= 0 ? storedName[(lastDot + 1)..] : storedName;
                if (targetDisplay.Length > 2 && targetDisplay[1] == ':')
                    targetDisplay = targetDisplay[2..];
            }
            else
            {
                continue; // No target info at all
            }

            var relKind = e.EdgeKind == 4 ? TypeRelationKind.BaseType : TypeRelationKind.Interface;
            relations.Add(new StoredTypeRelation(symbolId, SymbolId.From(targetFqn), relKind, targetDisplay));
        }

        return Task.FromResult<IReadOnlyList<StoredTypeRelation>>(relations);
    }

    public Task<IReadOnlyList<StoredTypeRelation>> GetDerivedTypesAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]);

        // Incoming Inherits(4) + Implements(5) edges = derived types
        var edges = merged.GetIncomingEdges(intId);
        var relations = new List<StoredTypeRelation>();
        foreach (var e in edges)
        {
            if (e.EdgeKind is not (4 or 5)) continue;
            if (e.FromSymbolIntId <= 0 || e.FromSymbolIntId > reader.SymbolCount) continue;
            ref readonly var source = ref reader.GetSymbolByIntId(e.FromSymbolIntId);
            var sourceFqn = reader.ResolveString(source.FqnStringId);
            var sourceDisplay = reader.ResolveString(source.DisplayNameStringId);
            var relKind = e.EdgeKind == 4 ? TypeRelationKind.BaseType : TypeRelationKind.Interface;
            relations.Add(new StoredTypeRelation(SymbolId.From(sourceFqn), symbolId, relKind, sourceDisplay));
        }

        return Task.FromResult<IReadOnlyList<StoredTypeRelation>>(relations);
    }

    // ── Fact queries ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<StoredFact>> GetFactsByKindAsync(RepoId repoId, CommitSha commitSha, FactKind kind, int limit, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var records = merged.GetFactsByKind((int)kind);

        // Convert all matching facts, then sort by (FilePath, LineStart) for deterministic
        // ordering that matches SQLite's rowid-based insertion order.
        var allFacts = new List<StoredFact>(records.Count);
        foreach (var r in records)
            allFacts.Add(RecordToCoreMappings.ToStoredFact(r, reader));

        // Sort by Value to match SQLite's ORDER BY f.value
        allFacts.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.Ordinal));

        IReadOnlyList<StoredFact> result = allFacts.Count <= limit ? allFacts : allFacts.GetRange(0, limit);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<StoredFact>> GetFactsForSymbolAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredFact>>([]);

        var records = merged.GetFactsBySymbol(intId);
        var facts = new List<StoredFact>(records.Count);
        foreach (var r in records)
            facts.Add(RecordToCoreMappings.ToStoredFact(r, reader));
        return Task.FromResult<IReadOnlyList<StoredFact>>(facts);
    }

    // ── File queries ─────────────────────────────────────────────────────────

    public Task<FileSpan?> GetFileSpanAsync(RepoId repoId, CommitSha commitSha, FilePath filePath, int startLine, int endLine, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var file = merged.GetFileByPath(filePath.Value);
        if (file == null) return Task.FromResult<FileSpan?>(null);

        string content;
        if (file.Value.ContentId > 0)
            content = reader.ResolveContent(file.Value.ContentId);
        else
            return Task.FromResult<FileSpan?>(null); // No content stored, no disk fallback in Phase 4

        var lines = content.Split('\n');
        var totalLines = lines.Length;
        var start = Math.Max(0, Math.Min(startLine - 1, totalLines));
        var end = Math.Max(start, Math.Min(totalLines, endLine));
        var spanLines = lines[start..end];
        var spanContent = string.Join('\n', spanLines);

        return Task.FromResult<FileSpan?>(new FileSpan(filePath, startLine, endLine, totalLines, spanContent, end < endLine));
    }

    public Task<IReadOnlyList<FilePath>> GetAllFilePathsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var paths = new List<FilePath>();
        foreach (var p in merged.EnumerateFilePaths())
            paths.Add(FilePath.From(p));
        return Task.FromResult<IReadOnlyList<FilePath>>(paths);
    }

    public Task<IReadOnlyList<(FilePath Path, string? Content)>> GetAllFileContentsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var result = new List<(FilePath, string?)>();
        foreach (var file in merged.EnumerateFiles())
        {
            var path = reader.ResolveString(file.PathStringId);
            var content = file.ContentId > 0 ? reader.ResolveContent(file.ContentId) : null;
            result.Add((FilePath.From(path), content));
        }
        // Sort by path for deterministic file iteration order (text search results
        // depend on which files are scanned first when a limit is applied).
        result.Sort((a, b) => string.Compare(a.Item1.Value, b.Item1.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<(FilePath Path, string? Content)>>(result);
    }

    // ── Stats/metadata queries ───────────────────────────────────────────────

    public Task<SemanticLevel?> GetSemanticLevelAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var baselineDir = BaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = ManifestWriter.Read(manifestPath);
        if (manifest?.ProjectDiagnostics is not { Count: > 0 } diags)
            return Task.FromResult<SemanticLevel?>(null);

        var compiledCount = diags.Count(d => d.Compiled);
        SemanticLevel level;
        if (compiledCount == diags.Count)
            level = SemanticLevel.Full;
        else if (compiledCount > 0)
            level = SemanticLevel.Partial;
        else
            level = SemanticLevel.SyntaxOnly;

        return Task.FromResult<SemanticLevel?>(level);
    }

    public Task<IReadOnlyList<SymbolSummary>> GetAllSymbolSummariesAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var summaries = new List<SymbolSummary>();
        foreach (var sym in merged.EnumerateSymbols())
            summaries.Add(RecordToCoreMappings.ToSummary(sym, reader));
        return Task.FromResult<IReadOnlyList<SymbolSummary>>(summaries);
    }

    public Task<string?> GetRepoRootAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var baselineDir = BaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = ManifestWriter.Read(manifestPath);
        return Task.FromResult(manifest?.RepoRootPath);
    }

    public Task<IReadOnlyList<ProjectDiagnostic>> GetProjectDiagnosticsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        // Read authoritative ProjectDiagnostics stored at build time (matches SQLite behavior).
        // These come from CompilationResult.Stats.ProjectDiagnostics, which Roslyn populates
        // with exact per-project counts during compilation.
        var baselineDir = BaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = ManifestWriter.Read(manifestPath);
        IReadOnlyList<ProjectDiagnostic> diags = manifest?.ProjectDiagnostics ?? [];
        return Task.FromResult(diags);
    }

    public Task<IReadOnlyList<UnresolvedEdge>> GetUnresolvedEdgesAsync(RepoId repoId, CommitSha commitSha, IReadOnlyList<FilePath> filePaths, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var fileIntIds = new HashSet<int>();
        foreach (var fp in filePaths)
        {
            var file = merged.GetFileByPath(fp.Value);
            if (file != null) fileIntIds.Add(file.Value.FileIntId);
        }

        var result = new List<UnresolvedEdge>();
        foreach (var edge in reader.EnumerateEdges())
        {
            if (edge.ResolutionState != 1) continue; // Only unresolved
            if (!fileIntIds.Contains(edge.FileIntId)) continue;

            var fromFqn = edge.FromSymbolIntId > 0 && edge.FromSymbolIntId <= reader.SymbolCount
                ? reader.ResolveString(reader.GetSymbolByIntId(edge.FromSymbolIntId).FqnStringId) : "";
            var toName = edge.ToNameStringId > 0 ? reader.ResolveString(edge.ToNameStringId) : null;
            var filePath = edge.FileIntId > 0 && edge.FileIntId <= reader.FileCount
                ? reader.ResolveString(reader.GetFileByIntId(edge.FileIntId).PathStringId) : "";
            var refKind = RecordToCoreMappings.ReverseEdgeKind(edge.EdgeKind).ToString();

            result.Add(new UnresolvedEdge(fromFqn, toName, null, refKind, filePath, edge.SpanStart, edge.SpanEnd));
        }

        return Task.FromResult<IReadOnlyList<UnresolvedEdge>>(result);
    }

    public Task<string?> GetDllFingerprintAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Not stored in v2 manifest yet

    // ── Overlay-write methods ──────────────────────────────────────────────────

    public async Task UpgradeEdgeAsync(RepoId repoId, CommitSha commitSha, EdgeUpgrade upgrade, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var overlay = GetOrCreateOverlay(commitSha.Value, reader);
        var fromIntId = ResolveSymbolIntId(merged, upgrade.FromSymbolId);
        var toIntId = ResolveSymbolIntId(merged, upgrade.ResolvedToSymbolId.Value);
        var file = merged.GetFileByPath(upgrade.FileId);
        var fileIntId = file?.FileIntId ?? 0;

        using var batch = overlay.BeginBatch();
        batch.ResolveEdge(fromIntId, fileIntId, upgrade.LocStart, toIntId);
        await batch.CommitAsync(ct);
    }

    public async Task<int> InsertMetadataStubsAsync(RepoId repoId, CommitSha commitSha, IReadOnlyList<SymbolCard> stubs, IReadOnlyList<ExtractedTypeRelation>? typeRelations = null, CancellationToken ct = default)
    {
        if (stubs.Count == 0) return 0;
        var (reader, _) = GetOrOpen(repoId.Value, commitSha.Value);
        var overlay = GetOrCreateOverlay(commitSha.Value, reader);

        using var batch = overlay.BeginBatch();
        foreach (var stub in stubs)
        {
            var fqn = stub.SymbolId.Value;
            var stableId = RecordMappers.ComputeDegradedStableId(stub.Kind, fqn, null);
            var stableIdSid = batch.InternString(stableId);
            var fqnSid = batch.InternString(fqn);
            var displayName = stub.FullyQualifiedName.Split('.')[^1];
            var displaySid = batch.InternString(displayName);
            var nsSid = batch.InternString(stub.Namespace ?? "");
            var tokenStr = string.Join(' ', SearchIndexBuilder.Tokenize(fqn, displayName, stub.Namespace));
            var tokensSid = batch.InternString(tokenStr);
            var tokens = tokenStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var intId = overlay.NextOverlaySymbolIntId--;
            var record = new SymbolRecord(intId, stableIdSid, fqnSid, displaySid, nsSid,
                0, 0, 0, RecordMappers.MapSymbolKind(stub.Kind), RecordMappers.MapAccessibility(stub.Visibility),
                1 << 7, // IsDecompiled flag
                stub.SpanStart, stub.SpanEnd, tokensSid, 0);
            batch.UpsertSymbol(record, tokens);
        }
        await batch.CommitAsync(ct);
        return stubs.Count;
    }

    public Task RebuildFtsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
        => Task.CompletedTask; // No FTS5 in v2 — search index is immutable

    public async Task InsertVirtualFileAsync(RepoId repoId, CommitSha commitSha, string virtualPath, string content, IReadOnlyList<ExtractedReference>? decompiledRefs = null, CancellationToken ct = default)
    {
        var (reader, _) = GetOrOpen(repoId.Value, commitSha.Value);
        var overlay = GetOrCreateOverlay(commitSha.Value, reader);

        using var batch = overlay.BeginBatch();
        var pathSid = batch.InternString(virtualPath);
        var normalizedSid = batch.InternString(virtualPath.ToLowerInvariant());
        var fileRecord = new FileRecord(overlay.NextOverlayFileIntId--, pathSid, normalizedSid,
            0, 0, 0, RecordMappers.DetectLanguage(virtualPath), 0, 0);
        batch.UpsertFile(fileRecord);
        await batch.CommitAsync(ct);
    }

    public async Task UpgradeDecompiledSymbolAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, string virtualFilePath, CancellationToken ct = default)
    {
        var (reader, merged) = GetOrOpen(repoId.Value, commitSha.Value);
        var overlay = GetOrCreateOverlay(commitSha.Value, reader);
        var rec = symbolId.Value.StartsWith("sym_", StringComparison.Ordinal)
            ? merged.GetSymbolByStableId(symbolId.Value)
            : merged.GetSymbolByFqn(symbolId.Value);
        if (rec == null) return;

        var sym = rec.Value;
        // Clear IsDecompiled flag (bit 7)
        var updatedFlags = sym.Flags & ~(1 << 7);
        var updated = new SymbolRecord(sym.SymbolIntId, sym.StableIdStringId, sym.FqnStringId,
            sym.DisplayNameStringId, sym.NamespaceStringId, sym.ContainerIntId, sym.FileIntId,
            sym.ProjectIntId, sym.Kind, sym.Accessibility, updatedFlags,
            sym.SpanStart, sym.SpanEnd, sym.NameTokensStringId, sym.SignatureHash);

        var stableId = merged.ResolveString(sym.StableIdStringId);
        var nameTokens = sym.NameTokensStringId > 0
            ? merged.ResolveString(sym.NameTokensStringId).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        using var batch = overlay.BeginBatch();
        batch.UpsertSymbol(updated, nameTokens);
        await batch.CommitAsync(ct);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    internal (EngineBaselineReader Reader, EngineMergedReader Merged) GetOrOpenBaseline(string repoId, string commitSha)
        => GetOrOpen(repoId, commitSha);

    private (EngineBaselineReader Reader, EngineMergedReader Merged) GetOrOpen(string repoId, string commitSha)
    {
        var cacheKey = CacheKey(repoId, commitSha);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var baselineDir = BaselineDir(repoId, commitSha);
            if (!File.Exists(Path.Combine(baselineDir, "manifest.json")))
                throw new StorageFormatException($"No baseline found for repo {repoId} commit {commitSha}");

            var reader = new EngineBaselineReader(baselineDir);

            // Init search index
            var searchPath = Path.Combine(baselineDir, "search.idx");
            if (File.Exists(searchPath))
                reader.InitSearch(new SearchIndexReader(reader, searchPath));

            // Init adjacency index
            var adjOutPath = Path.Combine(baselineDir, "adjacency-out.idx");
            var adjInPath = Path.Combine(baselineDir, "adjacency-in.idx");
            if (File.Exists(adjOutPath) && File.Exists(adjInPath))
                reader.InitAdjacency(new AdjacencyIndexReader(adjOutPath, adjInPath, reader.SymbolCount));

            var merged = new EngineMergedReader(reader);
            _cache[cacheKey] = (reader, merged);
            return (reader, merged);
        }
    }

    private static int ResolveSymbolIntId(EngineMergedReader merged, string symbolId)
    {
        if (symbolId.StartsWith("sym_", StringComparison.Ordinal))
        {
            var rec = merged.GetSymbolByStableId(symbolId);
            return rec?.SymbolIntId ?? 0;
        }
        else
        {
            var rec = merged.GetSymbolByFqn(symbolId);
            return rec?.SymbolIntId ?? 0;
        }
    }

    internal EngineOverlay GetOrCreateOverlay(string workspaceId, EngineBaselineReader reader)
    {
        lock (_cacheLock)
        {
            if (_overlays.TryGetValue(workspaceId, out var existing))
                return existing;

            var overlayDir = Path.Combine(_storeBaseDir, "overlays", workspaceId);
            var overlay = new EngineOverlay(overlayDir, workspaceId, reader);
            _overlays[workspaceId] = overlay;
            return overlay;
        }
    }

    internal bool OverlayExists(string workspaceId)
    {
        lock (_cacheLock)
        {
            if (_overlays.ContainsKey(workspaceId)) return true;
        }
        var manifestPath = Path.Combine(_storeBaseDir, "overlays", workspaceId, "manifest.json");
        return File.Exists(manifestPath);
    }

    internal EngineOverlay? TryGetOverlay(string workspaceId)
    {
        lock (_cacheLock)
        {
            return _overlays.GetValueOrDefault(workspaceId);
        }
    }

    internal void DeleteOverlay(string workspaceId)
    {
        lock (_cacheLock)
        {
            if (_overlays.Remove(workspaceId, out var overlay))
                overlay.Dispose();

            var overlayDir = Path.Combine(_storeBaseDir, "overlays", workspaceId);
            try
            {
                if (Directory.Exists(overlayDir))
                    Directory.Delete(overlayDir, recursive: true);
            }
            catch (IOException) { /* Best effort cleanup */ }
        }
    }

    // ── Path helpers ─────────────────────────────────────────────────────────

    private string RepoStoreDir(string repoId)
        => Path.Combine(_storeBaseDir, SanitizeRepoId(repoId));

    private string BaselineDir(string repoId, string commitSha)
        => Path.Combine(_storeBaseDir, SanitizeRepoId(repoId), "baselines", commitSha);

    private static string CacheKey(string repoId, string commitSha)
        => $"{repoId}|{commitSha}";

    private static string SanitizeRepoId(string repoId)
    {
        // Same sanitization as BaselineDbFactory: non-alphanumeric chars (except -, _) → _
        var chars = repoId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        }
        return new string(chars);
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_cacheLock)
        {
            foreach (var (reader, _) in _cache.Values)
                reader.Dispose();
            _cache.Clear();

            foreach (var overlay in _overlays.Values)
                overlay.Dispose();
            _overlays.Clear();
        }
    }
}
