namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Types;

/// <summary>
/// Upgrades unresolved reference edges to resolved when compilation state improves.
/// </summary>
public interface IResolutionWorker
{
    /// <summary>
    /// Attempt to resolve unresolved edges for the given repo/commit.
    /// Returns the count of edges successfully upgraded.
    /// Batch resolution — deferred in M04; returns 0.
    /// </summary>
    Task<int> ResolveEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve unresolved edges from specific files in the overlay, using the baseline
    /// symbol store for target symbol lookup. Called automatically after overlay refresh
    /// when compilation succeeds. No Roslyn types needed — uses stored symbol index.
    ///
    /// Design note: the Compilation-based variant (<see cref="ResolutionWorker.ResolveEdgesForFilesAsync"/>
    /// and <see cref="ResolutionWorker.ResolveOverlayEdgesForFilesAsync"/>) is on the
    /// concrete class only, since CodeMap.Core has zero dependencies.
    /// This method uses SearchSymbolsAsync for candidate lookup — equivalent quality
    /// since the baseline was indexed from the same (or compatible) compilation.
    /// </summary>
    Task<int> ResolveOverlayEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        WorkspaceId workspaceId,
        IReadOnlyList<FilePath> recompiledFiles,
        IOverlayStore overlayStore,
        ISymbolStore baselineStore,
        CancellationToken ct = default);
}
