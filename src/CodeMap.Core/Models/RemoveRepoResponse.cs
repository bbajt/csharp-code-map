namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>
/// Response for the <c>index_remove_repo</c> MCP tool.
/// </summary>
/// <param name="RepoId">The repository whose baselines were removed.</param>
/// <param name="BaselinesRemoved">Number of baselines deleted (or would-delete in dry-run).</param>
/// <param name="BytesFreed">Disk space freed in bytes (or would-free in dry-run).</param>
/// <param name="RemovedCommits">Commit SHAs of the baselines that were (or would be) removed.</param>
/// <param name="DryRun">True when no files were actually deleted.</param>
/// <param name="WorkspacesRemoved">
/// Workspace overlay directories that were (or would be) deleted with the repo (PHASE-21-10).
/// </param>
/// <param name="SkippedInUse">
/// Baselines left complete and in place because another process has them open; not counted as removed.
/// </param>
/// <param name="WorkspacesInUse">
/// Workspaces that another process holds open. When any are present nothing is deleted.
/// </param>
public record RemoveRepoResponse(
    RepoId RepoId,
    int BaselinesRemoved,
    long BytesFreed,
    IReadOnlyList<CommitSha> RemovedCommits,
    bool DryRun,
    IReadOnlyList<string>? WorkspacesRemoved = null,
    IReadOnlyList<CommitSha>? SkippedInUse = null,
    IReadOnlyList<string>? WorkspacesInUse = null);
