namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Manages the cached baseline .db files for a repository on disk.
/// Implemented by <c>EngineBaselineScanner</c> in <c>CodeMap.Storage.Engine</c>.
/// Kept separate from <see cref="ISymbolStore"/> because these are filesystem operations,
/// not database queries.
/// </summary>
public interface IBaselineScanner
{
    /// <summary>
    /// Returns metadata for all cached baseline .db files for the given repository.
    /// Files are sorted newest-first by creation time.
    /// </summary>
    Task<IReadOnlyList<BaselineInfo>> ListBaselinesAsync(
        RepoId repoId,
        CancellationToken ct = default);

    /// <summary>
    /// Removes ALL baselines for the given repository, freeing all disk space.
    /// Unlike <see cref="CleanupBaselinesAsync"/>, this ignores protection rules —
    /// HEAD and workspace-referenced baselines are also deleted.
    /// </summary>
    /// <param name="repoId">Repository whose entire baseline store to remove.</param>
    /// <param name="dryRun">If true, report what would be deleted without deleting (default true).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RemoveRepoResponse> RemoveRepoAsync(
        RepoId repoId,
        bool dryRun = true,
        CancellationToken ct = default);

    /// <summary>
    /// Removes old baselines according to retention rules. Protected baselines
    /// (current HEAD and workspace-referenced) are never deleted.
    /// </summary>
    /// <param name="repoId">Repository whose baselines to clean up.</param>
    /// <param name="currentHead">Current HEAD commit — always kept.</param>
    /// <param name="workspaceBaseCommits">Baseline SHAs referenced by active workspaces — always kept.</param>
    /// <param name="keepCount">Keep the N most-recently-created baselines (default 5).</param>
    /// <param name="olderThanDays">Remove baselines older than N days (optional).</param>
    /// <param name="dryRun">If true, report what would be deleted without deleting (default true).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CleanupResponse> CleanupBaselinesAsync(
        RepoId repoId,
        CommitSha currentHead,
        IReadOnlySet<CommitSha> workspaceBaseCommits,
        int keepCount = 5,
        int? olderThanDays = null,
        bool dryRun = true,
        CancellationToken ct = default);
}
