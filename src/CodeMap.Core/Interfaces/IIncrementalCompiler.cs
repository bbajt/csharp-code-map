namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Performs incremental recompilation of a changed set of files and produces
/// an <see cref="OverlayDelta"/> by diffing against the existing baseline.
/// Implementation: CodeMap.Roslyn.
/// </summary>
public interface IIncrementalCompiler : IDisposable
{
    /// <summary>
    /// Identifies which projects contain <paramref name="changedFiles"/>, recompiles
    /// only those projects, extracts symbols/refs from the changed files, and diffs
    /// against the baseline to produce a delta.
    /// </summary>
    /// <param name="solutionPath">Absolute path to the .sln file.</param>
    /// <param name="repoRootPath">Absolute path to the repository root (used for relativization).</param>
    /// <param name="changedFiles">Repo-relative paths of files that changed.</param>
    /// <param name="baselineStore">Store to query for baseline symbols to diff against.</param>
    /// <param name="repoId">Repo identity for baseline queries.</param>
    /// <param name="commitSha">Baseline commit SHA to diff against.</param>
    /// <param name="currentRevision">Current overlay revision (delta will carry revision + 1).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<OverlayDelta> ComputeDeltaAsync(
        string solutionPath,
        string repoRootPath,
        IReadOnlyList<FilePath> changedFiles,
        ISymbolStore baselineStore,
        RepoId repoId,
        CommitSha commitSha,
        int currentRevision,
        CancellationToken ct = default);
}
