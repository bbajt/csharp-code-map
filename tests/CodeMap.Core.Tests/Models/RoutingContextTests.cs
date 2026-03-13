namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class RoutingContextTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly WorkspaceId Ws = WorkspaceId.From("agent-1");

    [Fact]
    public void Committed_NoWorkspace_Succeeds()
    {
        var ctx = new RoutingContext(Repo);
        ctx.Consistency.Should().Be(ConsistencyMode.Committed);
        ctx.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public void Committed_WithWorkspace_Succeeds()
    {
        var ctx = new RoutingContext(Repo, Ws, ConsistencyMode.Committed);
        ctx.WorkspaceId.Should().Be(Ws);
    }

    [Fact]
    public void Workspace_WithWorkspace_Succeeds()
    {
        var ctx = new RoutingContext(Repo, Ws, ConsistencyMode.Workspace);
        ctx.Consistency.Should().Be(ConsistencyMode.Workspace);
    }

    [Fact]
    public void Workspace_NoWorkspace_ThrowsArgumentException() =>
        FluentActions.Invoking(() => new RoutingContext(Repo, null, ConsistencyMode.Workspace))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Ephemeral_WithWorkspace_Succeeds()
    {
        var ctx = new RoutingContext(Repo, Ws, ConsistencyMode.Ephemeral);
        ctx.Consistency.Should().Be(ConsistencyMode.Ephemeral);
    }

    [Fact]
    public void Ephemeral_NoWorkspace_ThrowsArgumentException() =>
        FluentActions.Invoking(() => new RoutingContext(Repo, null, ConsistencyMode.Ephemeral))
            .Should().Throw<ArgumentException>();
}
