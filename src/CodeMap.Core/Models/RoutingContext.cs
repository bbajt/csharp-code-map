namespace CodeMap.Core.Models;

/// <summary>
/// Routing context passed with every query to identify the target repo, workspace, and consistency mode.
/// </summary>
public record RoutingContext
{
    public Types.RepoId RepoId { get; init; }
    public Types.WorkspaceId? WorkspaceId { get; init; }
    public Enums.ConsistencyMode Consistency { get; init; }
    public Types.CommitSha? BaselineCommitSha { get; init; }

    /// <summary>
    /// In-memory virtual file contents for Ephemeral mode span queries.
    /// Null or empty for Committed and Workspace modes.
    /// </summary>
    public IReadOnlyList<VirtualFile>? VirtualFiles { get; init; }

    /// <summary>
    /// Validates routing rules:
    /// - Workspace mode requires WorkspaceId
    /// - Ephemeral mode requires WorkspaceId
    /// - Committed mode ignores WorkspaceId
    /// </summary>
    public RoutingContext(
        Types.RepoId repoId,
        Types.WorkspaceId? workspaceId = null,
        Enums.ConsistencyMode consistency = Enums.ConsistencyMode.Committed,
        Types.CommitSha? baselineCommitSha = null,
        IReadOnlyList<VirtualFile>? virtualFiles = null)
    {
        if (consistency == Enums.ConsistencyMode.Workspace && workspaceId is null)
            throw new ArgumentException("WorkspaceId is required for Workspace consistency mode.");
        if (consistency == Enums.ConsistencyMode.Ephemeral && workspaceId is null)
            throw new ArgumentException("WorkspaceId is required for Ephemeral consistency mode.");
        RepoId = repoId;
        WorkspaceId = workspaceId;
        Consistency = consistency;
        BaselineCommitSha = baselineCommitSha;
        VirtualFiles = virtualFiles;
    }
}
