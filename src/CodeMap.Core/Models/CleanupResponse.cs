namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>
/// Response for the <c>index_cleanup</c> MCP tool.
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
/// <param name="ProtectedByWorkspaces">
/// Baselines kept because a workspace of any CodeMap process is based on them (its overlay records the
/// baseline commit on disk; PHASE-21-10).
/// </param>
/// <param name="SkippedInUse">
/// Baselines left complete and in place because another process has them open; not counted as removed.
/// </param>
/// <param name="WorkspacesWithoutBaselineRecord">
/// Workspace overlays that don't record their baseline (created before v2.10.0), so their baseline
/// can't be protected. Delete stale ones with <c>workspace_delete</c>.
/// </param>
/// <param name="WorkspacesInUse">
/// Live workspaces (open in another process) whose baseline isn't recorded. When any are present a
/// non-dry-run cleanup deletes nothing.
/// </param>
public record CleanupResponse(
    int BaselinesRemoved,
    long BytesReclaimed,
    IReadOnlyList<CommitSha> RemovedCommits,
    IReadOnlyList<CommitSha> KeptCommits,
    bool DryRun,
    IReadOnlyList<CommitSha>? ProtectedByWorkspaces = null,
    IReadOnlyList<CommitSha>? SkippedInUse = null,
    IReadOnlyList<string>? WorkspacesWithoutBaselineRecord = null,
    IReadOnlyList<string>? WorkspacesInUse = null);
