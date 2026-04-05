namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>
/// Response for the <c>index.remove_repo</c> MCP tool.
/// </summary>
/// <param name="RepoId">The repository whose baselines were removed.</param>
/// <param name="BaselinesRemoved">Number of baselines deleted (or would-delete in dry-run).</param>
/// <param name="BytesFreed">Disk space freed in bytes (or would-free in dry-run).</param>
/// <param name="RemovedCommits">Commit SHAs of the baselines that were (or would be) removed.</param>
/// <param name="DryRun">True when no files were actually deleted.</param>
public record RemoveRepoResponse(
    RepoId RepoId,
    int BaselinesRemoved,
    long BytesFreed,
    IReadOnlyList<CommitSha> RemovedCommits,
    bool DryRun);
