namespace CodeMap.TestUtilities.Builders;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Fixtures;

/// <summary>
/// Fluent builder for creating RoutingContext instances in tests.
/// </summary>
public class RoutingContextBuilder
{
    private RepoId _repoId = TestConstants.SampleRepoId;
    private WorkspaceId? _workspaceId = null;
    private ConsistencyMode _consistency = ConsistencyMode.Committed;
    private CommitSha? _baselineCommitSha = null;

    public RoutingContextBuilder WithRepoId(string id) { _repoId = RepoId.From(id); return this; }
    public RoutingContextBuilder WithWorkspaceId(string id) { _workspaceId = WorkspaceId.From(id); return this; }
    public RoutingContextBuilder WithConsistency(ConsistencyMode mode) { _consistency = mode; return this; }
    public RoutingContextBuilder WithBaselineCommitSha(CommitSha sha) { _baselineCommitSha = sha; return this; }

    public RoutingContext Build() =>
        new(_repoId, _workspaceId, _consistency, _baselineCommitSha);
}
