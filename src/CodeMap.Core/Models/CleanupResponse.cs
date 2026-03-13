namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>
/// Response for the <c>index.cleanup</c> MCP tool.
/// </summary>
/// <param name="BaselinesRemoved">
/// Number of baselines deleted (or would-delete when <paramref name="DryRun"/> is true).
/// </param>
/// <param name="BytesReclaimed">
/// Disk space freed in bytes (or would-free in dry-run mode).
/// </param>
/// <param name="RemovedCommits">Commit SHAs of the baselines that were (or would be) removed.</param>
/// <param name="KeptCommits">Commit SHAs of the baselines that were retained.</param>
/// <param name="DryRun">True when no files were actually deleted.</param>
public record CleanupResponse(
    int BaselinesRemoved,
    long BytesReclaimed,
    IReadOnlyList<CommitSha> RemovedCommits,
    IReadOnlyList<CommitSha> KeptCommits,
    bool DryRun);
