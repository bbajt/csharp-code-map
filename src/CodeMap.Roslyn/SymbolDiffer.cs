namespace CodeMap.Roslyn;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Diffs newly extracted symbols from changed files against a baseline index
/// to produce an <see cref="OverlayDelta"/>.
/// </summary>
public sealed class SymbolDiffer
{
    private readonly ILogger<SymbolDiffer> _logger;

    public SymbolDiffer(ILogger<SymbolDiffer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Computes an overlay delta by comparing new symbols against the baseline.
    /// </summary>
    /// <param name="baselineStore">Baseline to diff against.</param>
    /// <param name="repoId">Repository identity.</param>
    /// <param name="commitSha">Baseline commit to compare with.</param>
    /// <param name="changedFiles">Files that were recompiled.</param>
    /// <param name="newSymbols">Symbols extracted from the recompiled files.</param>
    /// <param name="newRefs">References extracted from the recompiled files.</param>
    /// <param name="newFiles">File metadata for the recompiled files.</param>
    /// <param name="currentRevision">Current overlay revision (delta will carry revision + 1).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<OverlayDelta> ComputeDeltaAsync(
        ISymbolStore baselineStore,
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<FilePath> changedFiles,
        IReadOnlyList<SymbolCard> newSymbols,
        IReadOnlyList<ExtractedReference> newRefs,
        IReadOnlyList<ExtractedFile> newFiles,
        int currentRevision,
        CancellationToken ct = default)
    {
        // 1. Collect baseline symbols from all changed files
        var baselineSymbolIds = new HashSet<SymbolId>();

        foreach (var file in changedFiles)
        {
            ct.ThrowIfCancellationRequested();

            var symbols = await baselineStore.GetSymbolsByFileAsync(
                repoId, commitSha, file, ct);

            foreach (var s in symbols)
                baselineSymbolIds.Add(s.SymbolId);
        }

        _logger.LogDebug(
            "Baseline symbols in {FileCount} changed files: {Count}",
            changedFiles.Count, baselineSymbolIds.Count);

        // 2. Determine which baseline symbols were deleted
        var newSymbolIds = newSymbols.Select(s => s.SymbolId).ToHashSet();
        var deletedIds = baselineSymbolIds
            .Where(id => !newSymbolIds.Contains(id))
            .ToList();

        _logger.LogDebug(
            "Delta: +{Added} symbols, -{Deleted} deleted, rev {Rev}",
            newSymbols.Count, deletedIds.Count, currentRevision + 1);

        // 3. Build delta
        return new OverlayDelta(
            ReindexedFiles: newFiles,
            AddedOrUpdatedSymbols: newSymbols,
            DeletedSymbolIds: deletedIds,
            AddedOrUpdatedReferences: newRefs,
            DeletedReferenceFiles: changedFiles,
            NewRevision: currentRevision + 1);
    }
}
