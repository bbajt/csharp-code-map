namespace CodeMap.Core.Models;

/// <summary>
/// Summary of an active workspace overlay, returned by repo.status.
/// </summary>
public record WorkspaceSummary(
    Types.WorkspaceId WorkspaceId,
    Types.RepoId RepoId,
    Types.CommitSha BaselineCommitSha,
    int ChangedFileCount,
    int OverlaySymbolCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt
);
