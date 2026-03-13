namespace CodeMap.Query;

using CodeMap.Core.Types;

/// <summary>
/// In-memory state for an active workspace session.
/// Held in <see cref="WorkspaceManager"/>'s registry.
/// </summary>
public record WorkspaceInfo(
    WorkspaceId WorkspaceId,
    RepoId RepoId,
    CommitSha BaselineCommitSha,
    int CurrentRevision,
    string SolutionPath,
    string RepoRootPath,
    DateTimeOffset CreatedAt
);
