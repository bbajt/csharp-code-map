namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>
/// Metadata about a single cached baseline on disk.
/// </summary>
/// <param name="CommitSha">The commit SHA that identifies this baseline (also its filename).</param>
/// <param name="CreatedAt">When the baseline file was created (from filesystem metadata).</param>
/// <param name="SizeBytes">Size of the .db file in bytes.</param>
/// <param name="IsCurrentHead">True when this baseline matches the repository's current HEAD commit.</param>
/// <param name="IsActiveWorkspaceBase">True when at least one active workspace uses this baseline as its base.</param>
public record BaselineInfo(
    CommitSha CommitSha,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    bool IsCurrentHead,
    bool IsActiveWorkspaceBase);

/// <summary>
/// Response for the <c>index.list_baselines</c> MCP tool.
/// </summary>
/// <param name="RepoId">The repository whose baselines are listed.</param>
/// <param name="CurrentHead">Current HEAD commit SHA, or null if it could not be resolved.</param>
/// <param name="Baselines">All cached baselines, sorted newest-first by creation time.</param>
/// <param name="TotalSizeBytes">Sum of all baseline file sizes in bytes.</param>
public record ListBaselinesResponse(
    RepoId RepoId,
    CommitSha? CurrentHead,
    IReadOnlyList<BaselineInfo> Baselines,
    long TotalSizeBytes);
