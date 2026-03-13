namespace CodeMap.Query;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;

/// <summary>
/// Pure static helpers for merging baseline + overlay query results.
/// All methods are side-effect-free for easy unit testing.
/// </summary>
public static class MergeHelpers
{
    /// <summary>
    /// Merges baseline and overlay search hits using the overlay-wins strategy.
    ///
    /// Overlay hits appear first (higher priority — recently modified symbols).
    /// Baseline hits are included only if:
    ///   (a) their symbol_id is not in <paramref name="deletedIds"/>, AND
    ///   (b) their file_path is not in <paramref name="overlayFiles"/>
    ///       (any file that was reindexed in the overlay is fully superseded).
    /// </summary>
    public static MergedSearchResult MergeSearchResults(
        IReadOnlyList<SymbolSearchHit> baselineHits,
        IReadOnlyList<SymbolSearchHit> overlayHits,
        IReadOnlySet<SymbolId> deletedIds,
        IReadOnlySet<FilePath> overlayFiles,
        int limit)
    {
        // Filter baseline: exclude deleted symbols and symbols from reindexed files
        var filteredBaseline = baselineHits
            .Where(h => !deletedIds.Contains(h.SymbolId) && !overlayFiles.Contains(h.FilePath))
            .ToList();

        // Overlay first (higher priority), then filtered baseline; +1 for truncation detection
        var combined = overlayHits.Concat(filteredBaseline)
            .Take(limit + 1)
            .ToList();

        var truncated = combined.Count > limit;
        var hits = combined.Take(limit).ToList();
        var totalCount = truncated ? limit + 1 : hits.Count;

        return new MergedSearchResult(hits, totalCount, truncated);
    }
}

/// <summary>Result of merging baseline and overlay search hits.</summary>
public record MergedSearchResult(
    IReadOnlyList<SymbolSearchHit> Hits,
    int TotalCount,
    bool Truncated);
