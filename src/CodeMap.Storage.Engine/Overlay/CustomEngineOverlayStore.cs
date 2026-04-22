namespace CodeMap.Storage.Engine;

using System.Collections.Concurrent;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// IOverlayStore adapter wrapping EngineOverlay for WorkspaceManager + MergedQueryEngine.
/// Each workspace gets its own overlay directory keyed by workspaceId.
/// Bridges v2 binary records ↔ Core domain types.
/// </summary>
public sealed class CustomEngineOverlayStore : IOverlayStore
{
    private readonly CustomSymbolStore _symbolStore;
    private readonly string _storeBaseDir;

    // workspaceId → (baseCommitSha, repoId) mapping (set on CreateOverlayAsync)
    private readonly ConcurrentDictionary<string, (string CommitSha, string RepoId)> _wsToCommit = new(StringComparer.Ordinal);

    public CustomEngineOverlayStore(CustomSymbolStore symbolStore, string storeBaseDir)
    {
        _symbolStore = symbolStore;
        _storeBaseDir = storeBaseDir;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public Task CreateOverlayAsync(RepoId repoId, WorkspaceId workspaceId, CommitSha baselineCommitSha, CancellationToken ct = default)
    {
        _wsToCommit[workspaceId.Value] = (baselineCommitSha.Value, repoId.Value);

        var (reader, _) = _symbolStore.GetOrOpenBaseline(repoId.Value, baselineCommitSha.Value);
        _symbolStore.GetOrCreateOverlay(workspaceId.Value, reader);
        return Task.CompletedTask;
    }

    public async Task ApplyDeltaAsync(RepoId repoId, WorkspaceId workspaceId, OverlayDelta delta, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return;

        using var batch = overlay.BeginBatch();

        // Tombstone deleted symbols
        foreach (var deletedId in delta.DeletedSymbolIds)
        {
            var rec = reader.GetSymbolByFqn(deletedId.Value);
            string? stableId = null;
            if (rec != null && rec.Value.StableIdStringId > 0)
                stableId = reader.ResolveString(rec.Value.StableIdStringId);
            if (stableId != null)
                batch.Tombstone(0, rec?.SymbolIntId ?? 0, stableId);
        }

        // Upsert files
        foreach (var file in delta.ReindexedFiles)
        {
            var pathSid = batch.InternString(file.Path.Value);
            var normalizedSid = batch.InternString(file.Path.Value.ToLowerInvariant());
            var (hashHigh, hashLow) = RecordMappers.SplitSha256(file.Sha256Hash);
            var fileRecord = new FileRecord(overlay.NextOverlayFileIntId--,
                pathSid, normalizedSid, 0, hashHigh, hashLow,
                RecordMappers.DetectLanguage(file.Path.Value), 0, 0);
            batch.UpsertFile(fileRecord);
        }

        // Upsert symbols
        foreach (var sym in delta.AddedOrUpdatedSymbols)
        {
            var fqn = sym.SymbolId.Value;
            var stableId = RecordMappers.ComputeDegradedStableId(sym.Kind, fqn, null);
            var stableIdSid = batch.InternString(stableId);
            var fqnSid = batch.InternString(fqn);
            var displayName = sym.FullyQualifiedName.Split('.')[^1];
            var displaySid = batch.InternString(displayName);
            var nsSid = batch.InternString(sym.Namespace ?? "");
            var tokenStr = string.Join(' ', SearchIndexBuilder.Tokenize(fqn, displayName, sym.Namespace));
            var tokensSid = batch.InternString(tokenStr);
            var tokens = tokenStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var intId = overlay.NextOverlaySymbolIntId--;
            var record = new SymbolRecord(intId, stableIdSid, fqnSid, displaySid, nsSid,
                0, 0, 0, RecordMappers.MapSymbolKind(sym.Kind),
                RecordMappers.MapAccessibility(sym.Visibility),
                RecordMappers.BuildSymbolFlags(sym),
                sym.SpanStart, sym.SpanEnd, tokensSid, 0);
            batch.UpsertSymbol(record, tokens);
        }

        // Add references as edges
        foreach (var r in delta.AddedOrUpdatedReferences)
        {
            var fromIntId = ResolveIntId(reader, overlay, r.FromSymbol.Value);
            var toIntId = ResolveIntId(reader, overlay, r.ToSymbol.Value);
            var toNameSid = !string.IsNullOrEmpty(r.ToName) ? batch.InternString(r.ToName) : 0;
            var edge = new EdgeRecord(overlay.NextOverlayEdgeIntId--, fromIntId, toIntId, toNameSid,
                0, r.LineStart, r.LineEnd, RecordMappers.MapEdgeKind(r.Kind),
                RecordMappers.MapResolutionState(r.ResolutionState),
                r.IsDecompiled ? 1 : 0, 1);
            batch.AddEdge(edge);
        }

        // Add type relations as edges (Inherits=4, Implements=5)
        if (delta.TypeRelations != null)
        {
            foreach (var tr in delta.TypeRelations)
            {
                var fromIntId = ResolveIntId(reader, overlay, tr.TypeSymbolId.Value);
                var toIntId = ResolveIntId(reader, overlay, tr.RelatedSymbolId.Value);
                short edgeKind = tr.RelationKind == TypeRelationKind.BaseType ? (short)4 : (short)5;
                var edge = new EdgeRecord(overlay.NextOverlayEdgeIntId--, fromIntId, toIntId, 0,
                    0, 0, 0, edgeKind, 0, 0, 1);
                batch.AddEdge(edge);
            }
        }

        // Add facts
        if (delta.Facts != null)
        {
            foreach (var f in delta.Facts)
            {
                var ownerIntId = ResolveIntId(reader, overlay, f.SymbolId.Value);
                var (primary, secondary) = RecordMappers.SplitFactValue(f.Value);
                var primarySid = batch.InternString(primary);
                var secondarySid = batch.InternString(secondary);
                var fact = new FactRecord(overlay.NextOverlayFactIntId--, ownerIntId, 0,
                    f.LineStart, f.LineEnd, RecordMappers.MapFactKind(f.Kind),
                    primarySid, secondarySid, RecordMappers.MapConfidence(f.Confidence), 0);
                batch.AddFact(fact);
            }
        }

        await batch.CommitAsync(ct);
    }

    public Task ResetOverlayAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
    {
        var found = _wsToCommit.TryGetValue(workspaceId.Value, out var entry);

        _symbolStore.DeleteOverlay(workspaceId.Value);
        if (found)
        {
            var (reader, _) = _symbolStore.GetOrOpenBaseline(entry.RepoId, entry.CommitSha);
            _symbolStore.GetOrCreateOverlay(workspaceId.Value, reader);
        }
        return Task.CompletedTask;
    }

    public Task DeleteOverlayAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
    {
        _symbolStore.DeleteOverlay(workspaceId.Value);
        _wsToCommit.TryRemove(workspaceId.Value, out _);
        return Task.CompletedTask;
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    public Task<bool> OverlayExistsAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
        => Task.FromResult(_symbolStore.OverlayExists(workspaceId.Value));

    public Task<int> GetRevisionAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
    {
        var overlay = _symbolStore.TryGetOverlay(workspaceId.Value);
        return Task.FromResult(overlay?.Revision ?? 0);
    }

    public Task<SymbolCard?> GetOverlaySymbolAsync(RepoId repoId, WorkspaceId workspaceId, SymbolId symbolId, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return Task.FromResult<SymbolCard?>(null);

        SymbolRecord? rec;
        if (symbolId.Value.StartsWith("sym_", StringComparison.Ordinal))
            rec = overlay.TryGetOverlaySymbol(symbolId.Value, out _);
        else
        {
            rec = null;
            foreach (var sym in overlay.GetOverlayNewSymbols())
            {
                if (overlay.ResolveString(sym.FqnStringId) == symbolId.Value) { rec = sym; break; }
            }
        }

        if (rec == null) return Task.FromResult<SymbolCard?>(null);
        // Overlay-local symbols may have StringIds beyond the baseline dictionary range.
        // Use overlay.ResolveString which handles both baseline and overlay string pools.
        return Task.FromResult<SymbolCard?>(RecordToCoreMappings.ToSymbolCard(rec.Value, reader, overlay.ResolveString));
    }

    public Task<IReadOnlyList<SymbolSearchHit>> SearchOverlaySymbolsAsync(RepoId repoId, WorkspaceId workspaceId, string query, SymbolSearchFilters? filters, int limit, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null)
            return Task.FromResult<IReadOnlyList<SymbolSearchHit>>([]);

        // Tokenize query the same way symbols are tokenized at index time
        var queryTokens = query.ToLowerInvariant()
            .Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.Length == 0)
            return Task.FromResult<IReadOnlyList<SymbolSearchHit>>([]);

        // Find overlay symbols matching ALL query tokens (AND semantics)
        HashSet<int>? matchedIntIds = null;
        foreach (var token in queryTokens)
        {
            var tokenHits = overlay.GetOverlaySymbolsForTokenPrefix(token);
            if (tokenHits.Count == 0)
                return Task.FromResult<IReadOnlyList<SymbolSearchHit>>([]);

            var hitSet = new HashSet<int>(tokenHits);
            matchedIntIds = matchedIntIds == null ? hitSet : [.. matchedIntIds.Intersect(hitSet)];

            if (matchedIntIds.Count == 0)
                return Task.FromResult<IReadOnlyList<SymbolSearchHit>>([]);
        }

        // Convert matched IntIds to SymbolSearchHit
        var results = new List<SymbolSearchHit>();
        foreach (var sym in overlay.GetOverlayNewSymbols())
        {
            if (!matchedIntIds!.Contains(sym.SymbolIntId)) continue;

            // Apply kind filter if specified
            if (filters?.Kinds is { Count: > 0 } kinds)
            {
                var symKind = RecordToCoreMappings.ReverseSymbolKind(sym.Kind);
                if (!kinds.Contains(symKind)) continue;
            }

            var fqn = overlay.ResolveString(sym.FqnStringId);
            var displayName = overlay.ResolveString(sym.DisplayNameStringId);
            var ns = overlay.ResolveString(sym.NamespaceStringId);

            // Apply namespace filter if specified
            if (filters?.Namespace is { Length: > 0 } nsFilter
                && !ns.StartsWith(nsFilter, StringComparison.Ordinal))
                continue;

            // File path filter: resolve from overlay file records or baseline file table.
            if (filters?.FilePath is { Length: > 0 } fpFilter)
            {
                var path = ResolveOverlaySymbolFilePath(overlay, reader, sym.FileIntId);
                if (path is null || !path.StartsWith(fpFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // Project name filter: overlay-new symbols inherit project from their file.
            if (filters?.ProjectName is { Length: > 0 } projFilter)
            {
                var projName = ResolveOverlaySymbolProjectName(overlay, reader, sym.FileIntId);
                if (projName is null || !string.Equals(projName, projFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            results.Add(new SymbolSearchHit(
                SymbolId: SymbolId.From(fqn),
                FullyQualifiedName: fqn,
                Kind: RecordToCoreMappings.ReverseSymbolKind(sym.Kind),
                Signature: displayName,
                DocumentationSnippet: null,
                FilePath: FilePath.From("overlay"),
                Line: sym.SpanStart,
                Score: 1.0));

            if (results.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<SymbolSearchHit>>(results);
    }

    /// <summary>
    /// Resolves the file path for an overlay-new symbol. Positive FileIntId points into
    /// the baseline file table; negative (or zero-miss) means an overlay-local new file
    /// whose FileRecord is only reachable via the path-keyed dictionary.
    /// </summary>
    private static string? ResolveOverlaySymbolFilePath(EngineOverlay overlay, EngineBaselineReader reader, int fileIntId)
    {
        if (fileIntId >= 1 && fileIntId <= reader.FileCount)
        {
            ref readonly var file = ref reader.GetFileByIntId(fileIntId);
            return file.PathStringId > 0 ? reader.ResolveString(file.PathStringId) : null;
        }
        foreach (var kv in overlay.FilesByPath)
            if (kv.Value.FileIntId == fileIntId) return kv.Key;
        return null;
    }

    private static string? ResolveOverlaySymbolProjectName(EngineOverlay overlay, EngineBaselineReader reader, int fileIntId)
    {
        int projectIntId;
        if (fileIntId >= 1 && fileIntId <= reader.FileCount)
        {
            projectIntId = reader.GetFileByIntId(fileIntId).ProjectIntId;
        }
        else
        {
            projectIntId = 0;
            foreach (var kv in overlay.FilesByPath)
                if (kv.Value.FileIntId == fileIntId) { projectIntId = kv.Value.ProjectIntId; break; }
        }
        if (projectIntId < 1 || projectIntId > reader.ProjectCount) return null;
        ref readonly var proj = ref reader.GetProjectByIntId(projectIntId);
        return proj.NameStringId > 0 ? reader.ResolveString(proj.NameStringId) : null;
    }

    public Task<IReadOnlyList<StoredReference>> GetOverlayReferencesAsync(RepoId repoId, WorkspaceId workspaceId, SymbolId symbolId, RefKind? kind, int limit, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return Task.FromResult<IReadOnlyList<StoredReference>>([]);
        var intId = ResolveIntId(reader, overlay, symbolId.Value);
        var edges = overlay.GetOverlayIncomingEdges(intId);
        var refs = new List<StoredReference>();
        foreach (var e in edges)
        {
            if (kind.HasValue && e.EdgeKind != RecordMappers.MapEdgeKind(kind.Value)) continue;
            refs.Add(RecordToCoreMappings.ToStoredReference(e, reader, overlay.ResolveString));
            if (refs.Count >= limit) break;
        }
        return Task.FromResult<IReadOnlyList<StoredReference>>(refs);
    }

    public Task<IReadOnlySet<SymbolId>> GetDeletedSymbolIdsAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>());
        var result = new HashSet<SymbolId>();
        foreach (var stableId in overlay.Tombstones)
        {
            var rec = reader.GetSymbolByStableId(stableId);
            if (rec != null)
                result.Add(SymbolId.From(reader.ResolveString(rec.Value.FqnStringId)));
        }
        return Task.FromResult<IReadOnlySet<SymbolId>>(result);
    }

    public Task<IReadOnlySet<FilePath>> GetOverlayFilePathsAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
    {
        var (overlay, _) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null) return Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>());
        var snapshot = overlay.GetFilePathsSnapshot();
        var paths = new HashSet<FilePath>(
            snapshot.Where(p => !string.IsNullOrWhiteSpace(p)).Select(FilePath.From));
        return Task.FromResult<IReadOnlySet<FilePath>>(paths);
    }

    public Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingOverlayReferencesAsync(RepoId repoId, WorkspaceId workspaceId, SymbolId symbolId, RefKind? kind, int limit, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return Task.FromResult<IReadOnlyList<StoredOutgoingReference>>([]);
        var intId = ResolveIntId(reader, overlay, symbolId.Value);
        var edges = overlay.GetOverlayOutgoingEdges(intId);
        var refs = new List<StoredOutgoingReference>();
        foreach (var e in edges)
        {
            if (kind.HasValue && e.EdgeKind != RecordMappers.MapEdgeKind(kind.Value)) continue;
            refs.Add(RecordToCoreMappings.ToOutgoingReference(e, reader, overlay.ResolveString));
            if (refs.Count >= limit) break;
        }
        return Task.FromResult<IReadOnlyList<StoredOutgoingReference>>(refs);
    }

    public Task<IReadOnlyList<StoredTypeRelation>> GetOverlayTypeRelationsAsync(RepoId repoId, WorkspaceId workspaceId, SymbolId symbolId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]);

    public Task<IReadOnlyList<StoredTypeRelation>> GetOverlayDerivedTypesAsync(RepoId repoId, WorkspaceId workspaceId, SymbolId symbolId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]);

    public Task<SemanticLevel?> GetOverlaySemanticLevelAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
        => Task.FromResult<SemanticLevel?>(SemanticLevel.Full);

    public Task<SymbolCard?> GetSymbolByStableIdAsync(RepoId repoId, WorkspaceId workspaceId, StableId stableId, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return Task.FromResult<SymbolCard?>(null);
        var rec = overlay.TryGetOverlaySymbol(stableId.Value, out var tombstoned);
        if (rec == null || tombstoned) return Task.FromResult<SymbolCard?>(null);
        return Task.FromResult<SymbolCard?>(RecordToCoreMappings.ToSymbolCard(rec.Value, reader, overlay.ResolveString));
    }

    public Task<IReadOnlyList<StoredFact>> GetOverlayFactsByKindAsync(RepoId repoId, WorkspaceId workspaceId, FactKind kind, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredFact>>([]);

    public Task<IReadOnlyList<StoredFact>> GetOverlayFactsForSymbolAsync(RepoId repoId, WorkspaceId workspaceId, SymbolId symbolId, CancellationToken ct = default)
    {
        var (overlay, reader) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null || reader == null) return Task.FromResult<IReadOnlyList<StoredFact>>([]);
        var intId = ResolveIntId(reader, overlay, symbolId.Value);
        var facts = overlay.GetOverlayFacts(intId);
        var result = new List<StoredFact>(facts.Count);
        foreach (var f in facts)
            result.Add(RecordToCoreMappings.ToStoredFact(f, reader, overlay.ResolveString));
        return Task.FromResult<IReadOnlyList<StoredFact>>(result);
    }

    public Task<int> GetOverlayFactCountAsync(RepoId repoId, WorkspaceId workspaceId, CancellationToken ct = default)
    {
        var (overlay, _) = GetOverlayAndReader(workspaceId.Value);
        if (overlay == null) return Task.FromResult(0);
        return Task.FromResult(overlay.GetFactCount());
    }

    public Task<IReadOnlyList<UnresolvedEdge>> GetOverlayUnresolvedEdgesAsync(RepoId repoId, WorkspaceId workspaceId, IReadOnlyList<FilePath> filePaths, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UnresolvedEdge>>([]);

    public Task UpgradeOverlayEdgeAsync(RepoId repoId, WorkspaceId workspaceId, EdgeUpgrade upgrade, CancellationToken ct = default)
    {
        if (!_wsToCommit.TryGetValue(workspaceId.Value, out var entry))
            return Task.CompletedTask;
        return _symbolStore.UpgradeEdgeAsync(repoId, CommitSha.From(entry.CommitSha), upgrade, ct);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private (EngineOverlay? Overlay, EngineBaselineReader? Reader) GetOverlayAndReader(string workspaceId)
    {
        if (!_wsToCommit.TryGetValue(workspaceId, out var entry))
            return (null, null);

        var (reader, _) = _symbolStore.GetOrOpenBaseline(entry.RepoId, entry.CommitSha);
        var overlay = _symbolStore.TryGetOverlay(workspaceId);
        return (overlay, reader);
    }

    private static int ResolveIntId(EngineBaselineReader reader, EngineOverlay overlay, string symbolId)
    {
        var rec = reader.GetSymbolByFqn(symbolId);
        if (rec != null) return rec.Value.SymbolIntId;
        foreach (var sym in overlay.GetOverlayNewSymbols())
        {
            if (overlay.ResolveString(sym.FqnStringId) == symbolId) return sym.SymbolIntId;
        }
        return 0;
    }
}
