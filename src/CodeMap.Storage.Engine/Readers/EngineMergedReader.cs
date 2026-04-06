namespace CodeMap.Storage.Engine;

/// <summary>
/// Merged query view: baseline + overlay. When overlay is null, all queries delegate
/// to baseline directly. When overlay is present, tombstones hide baseline entities,
/// overlay replacements shadow same-StableId baseline entities, overlay-local entities
/// (negative IntIds) are included, unshadowed baseline entities remain visible.
/// Thread-safe.
/// </summary>
internal sealed class EngineMergedReader : IEngineMergedReader
{
    public EngineMergedReader(EngineBaselineReader baseline, IEngineOverlay? overlay = null)
    {
        Baseline = baseline;
        Overlay = overlay;
    }

    public IEngineBaselineReader Baseline { get; }
    public IEngineOverlay? Overlay { get; }

    // ── Symbol lookup ────────────────────────────────────────────────────────

    public SymbolRecord? GetSymbolByStableId(string stableId)
    {
        if (Overlay != null)
        {
            var overlaySym = Overlay.TryGetOverlaySymbol(stableId, out var tombstoned);
            if (tombstoned) return null;
            if (overlaySym != null) return overlaySym;
        }
        return Baseline.GetSymbolByStableId(stableId);
    }

    public SymbolRecord? GetSymbolByFqn(string fqn)
    {
        var baselineRec = Baseline.GetSymbolByFqn(fqn);
        if (baselineRec == null && Overlay != null)
        {
            // Search overlay new symbols by FQN — route StringId correctly
            foreach (var sym in Overlay.GetOverlayNewSymbols())
            {
                var symFqn = ResolveString(sym.FqnStringId);
                if (symFqn == fqn) return sym;
            }
            return null;
        }
        if (baselineRec == null) return null;

        // Check if tombstoned or replaced by overlay
        if (Overlay != null && baselineRec.Value.StableIdStringId > 0)
        {
            var stableId = Baseline.Dictionary.Resolve(baselineRec.Value.StableIdStringId);
            var overlaySym = Overlay.TryGetOverlaySymbol(stableId, out var tombstoned);
            if (tombstoned) return null;
            if (overlaySym != null) return overlaySym;
        }

        return baselineRec;
    }

    public SymbolRecord? GetSymbolByIntId(int symbolIntId)
    {
        if (symbolIntId < 0 && Overlay != null)
        {
            // Overlay-local symbol (negative IntId)
            foreach (var sym in Overlay.GetOverlayNewSymbols())
            {
                if (sym.SymbolIntId == symbolIntId) return sym;
            }
            return null;
        }
        if (symbolIntId < 1 || symbolIntId > Baseline.SymbolCount) return null;
        return Baseline.GetSymbolByIntId(symbolIntId);
    }

    // ── Symbol enumeration ───────────────────────────────────────────────────

    public IEnumerable<SymbolRecord> EnumerateSymbols(short? kindFilter = null,
        bool excludeDecompiled = false, bool excludeTestSymbols = false)
    {
        var tombstones = Overlay?.Tombstones;

        // Collect all StableIds that overlay owns (replacements + new symbols + tombstones)
        // so we can skip them from baseline enumeration to avoid duplicates
        var overlayStableIds = new HashSet<string>(StringComparer.Ordinal);

        if (Overlay != null)
        {
            // Add tombstoned StableIds (these hide baseline symbols)
            if (tombstones != null)
                foreach (var ts in tombstones)
                    overlayStableIds.Add(ts);

            // Yield overlay new symbols (negative IntIds) and collect their StableIds
            foreach (var sym in Overlay.GetOverlayNewSymbols())
            {
                if (sym.StableIdStringId > 0)
                    overlayStableIds.Add(ResolveString(sym.StableIdStringId));

                if (kindFilter.HasValue && sym.Kind != kindFilter.Value) continue;
                if (excludeDecompiled && (sym.Flags & (1 << 7)) != 0) continue;
                if (excludeTestSymbols && (sym.Flags & (1 << 8)) != 0) continue;
                yield return sym;
            }
        }

        // Yield baseline symbols — skip tombstoned and overlay-replaced
        foreach (var sym in Baseline.EnumerateSymbols())
        {
            if (kindFilter.HasValue && sym.Kind != kindFilter.Value) continue;
            if (excludeDecompiled && (sym.Flags & (1 << 7)) != 0) continue;
            if (excludeTestSymbols && (sym.Flags & (1 << 8)) != 0) continue;

            if (overlayStableIds.Count > 0 && sym.StableIdStringId > 0)
            {
                var stableId = Baseline.Dictionary.Resolve(sym.StableIdStringId);
                if (overlayStableIds.Contains(stableId)) continue;
            }

            yield return sym;
        }
    }

    public IReadOnlyList<SymbolRecord> GetSymbolsByFile(string repoRelativePath)
    {
        var file = Baseline.GetFileByPath(repoRelativePath);
        if (file == null) return [];
        var baselineSymbols = Baseline.GetSymbolsByFile(file.Value.FileIntId);

        if (Overlay == null) return baselineSymbols;

        // Filter out tombstoned symbols
        var tombstones = Overlay.Tombstones;
        var result = new List<SymbolRecord>();
        foreach (var sym in baselineSymbols)
        {
            if (sym.StableIdStringId > 0)
            {
                var stableId = Baseline.Dictionary.Resolve(sym.StableIdStringId);
                if (tombstones.Contains(stableId)) continue;
                // Check if overlay has replacement
                var overlaySym = Overlay.TryGetOverlaySymbol(stableId, out _);
                result.Add(overlaySym ?? sym);
            }
            else
            {
                result.Add(sym);
            }
        }

        // Add overlay new symbols for this file (if any have matching file path)
        // Overlay new symbols don't have a FileIntId in the baseline file set
        // — they reference overlay files. Skip for now (Phase 5 may extend).

        return result;
    }

    // ── File lookup ──────────────────────────────────────────────────────────

    public FileRecord? GetFileByPath(string repoRelativePath)
    {
        if (Overlay != null)
        {
            var overlayFile = Overlay.TryGetOverlayFile(repoRelativePath);
            if (overlayFile != null) return overlayFile;
        }
        return Baseline.GetFileByPath(repoRelativePath);
    }

    public IEnumerable<FileRecord> EnumerateFiles()
        => Baseline.EnumerateFiles();

    public IEnumerable<string> EnumerateFilePaths()
    {
        foreach (var file in Baseline.EnumerateFiles())
        {
            if (file.PathStringId > 0)
                yield return Baseline.Dictionary.Resolve(file.PathStringId);
        }
    }

    // ── Edge traversal ───────────────────────────────────────────────────────

    public IReadOnlyList<EdgeRecord> GetOutgoingEdges(int symbolIntId, EdgeFilter filter = default)
    {
        var baselineEdges = Baseline.GetOutgoingEdges(symbolIntId, filter);

        if (Overlay == null) return baselineEdges;

        var overlayEdges = Overlay.GetOverlayOutgoingEdges(symbolIntId);
        if (overlayEdges.Count == 0) return baselineEdges;

        var merged = new List<EdgeRecord>(baselineEdges.Count + overlayEdges.Count);
        merged.AddRange(baselineEdges);
        foreach (var e in overlayEdges)
        {
            if (filter.EdgeKind.HasValue && e.EdgeKind != filter.EdgeKind.Value) continue;
            if (filter.ResolvedOnly && e.ResolutionState != 0) continue;
            merged.Add(e);
        }
        return merged;
    }

    public IReadOnlyList<EdgeRecord> GetIncomingEdges(int symbolIntId, EdgeFilter filter = default)
    {
        var baselineEdges = Baseline.GetIncomingEdges(symbolIntId, filter);

        if (Overlay == null) return baselineEdges;

        var overlayEdges = Overlay.GetOverlayIncomingEdges(symbolIntId);
        if (overlayEdges.Count == 0) return baselineEdges;

        var merged = new List<EdgeRecord>(baselineEdges.Count + overlayEdges.Count);
        merged.AddRange(baselineEdges);
        foreach (var e in overlayEdges)
        {
            if (filter.EdgeKind.HasValue && e.EdgeKind != filter.EdgeKind.Value) continue;
            if (filter.ResolvedOnly && e.ResolutionState != 0) continue;
            merged.Add(e);
        }
        return merged;
    }

    // ── Fact lookup ──────────────────────────────────────────────────────────

    public IReadOnlyList<FactRecord> GetFactsBySymbol(int symbolIntId)
    {
        if (Overlay != null)
        {
            var overlayFacts = Overlay.GetOverlayFacts(symbolIntId);
            if (overlayFacts.Count > 0) return overlayFacts; // Overlay-wins
        }
        return Baseline.GetFactsBySymbol(symbolIntId);
    }

    public IReadOnlyList<FactRecord> GetFactsByKind(int factKind)
    {
        // Baseline facts of this kind (always available)
        var baselineFacts = Baseline.GetFactsByKind(factKind);
        if (Overlay == null) return baselineFacts;

        // For overlay, we don't have a by-kind index — return baseline only
        // (overlay facts are per-symbol, merged via GetFactsBySymbol overlay-wins)
        return baselineFacts;
    }

    // ── Search ───────────────────────────────────────────────────────────────

    public SymbolSearchResult[] SearchSymbols(string query, SymbolSearchFilter filter)
    {
        var baselineResults = Baseline.Search.SearchSymbols(query, filter);

        if (Overlay == null) return baselineResults;

        // Baseline results filtered by tombstones. Overlay-only symbols are
        // searched separately via CustomEngineOverlayStore.SearchOverlaySymbolsAsync
        // and merged by MergedQueryEngine.
        var tombstones = Overlay.Tombstones;
        if (tombstones.Count == 0) return baselineResults;

        return baselineResults.Where(r =>
        {
            if (r.Symbol.StableIdStringId <= 0) return true;
            var stableId = Baseline.Dictionary.Resolve(r.Symbol.StableIdStringId);
            return !tombstones.Contains(stableId);
        }).ToArray();
    }

    public (TextMatch[] Matches, bool IsTruncated) SearchText(string pattern, TextSearchFilter filter)
        => Baseline.Search.SearchText(pattern, filter);

    // ── String resolution ────────────────────────────────────────────────────

    public string ResolveString(int stringId)
    {
        if (stringId <= 0) return string.Empty;
        if (Overlay != null && stringId > ((EngineOverlay)Overlay).NBaselineStringIds)
            return Overlay.ResolveString(stringId);
        return Baseline.Dictionary.Resolve(stringId);
    }
}
