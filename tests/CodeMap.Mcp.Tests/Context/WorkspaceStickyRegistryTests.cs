namespace CodeMap.Mcp.Tests.Context;

using CodeMap.Mcp.Context;
using FluentAssertions;

public sealed class WorkspaceStickyRegistryTests
{
    [Fact]
    public void Get_Unset_ReturnsNull()
    {
        var r = new WorkspaceStickyRegistry();
        r.Get("/repo").Should().BeNull();
    }

    [Fact]
    public void Set_Then_Get_ReturnsValue()
    {
        var r = new WorkspaceStickyRegistry();
        r.Set("/repo", "ws-1");
        r.Get("/repo").Should().Be("ws-1");
    }

    [Fact]
    public void Set_OverwritesPrevious()
    {
        // Most-recent wins — matches the "latest workspace.create is sticky" rule.
        var r = new WorkspaceStickyRegistry();
        r.Set("/repo", "ws-1");
        r.Set("/repo", "ws-2");
        r.Get("/repo").Should().Be("ws-2");
    }

    [Fact]
    public void Clear_MatchingWorkspace_RemovesEntry()
    {
        var r = new WorkspaceStickyRegistry();
        r.Set("/repo", "ws-1");
        r.Clear("/repo", "ws-1");
        r.Get("/repo").Should().BeNull();
    }

    [Fact]
    public void Clear_NonMatchingWorkspace_IsNoOp()
    {
        // Deleting a non-sticky workspace must not clear an unrelated sticky default.
        var r = new WorkspaceStickyRegistry();
        r.Set("/repo", "ws-1");
        r.Clear("/repo", "ws-other");
        r.Get("/repo").Should().Be("ws-1");
    }

    [Fact]
    public void Set_IndependentPerRepo()
    {
        var r = new WorkspaceStickyRegistry();
        r.Set("/repo-a", "ws-a1");
        r.Set("/repo-b", "ws-b1");
        r.Get("/repo-a").Should().Be("ws-a1");
        r.Get("/repo-b").Should().Be("ws-b1");
    }

    [Fact]
    public void PathVariants_ResolveToSameEntry()
    {
        // Same normalization as RepoRegistry so both registries agree on identity.
        var r = new WorkspaceStickyRegistry();
        var cwd = Directory.GetCurrentDirectory();
        r.Set(Path.Combine(cwd, "Foo"), "ws-1");
        r.Get(Path.Combine(cwd, "./Foo/")).Should().Be("ws-1");
    }

    [Fact]
    public void Set_EmptyArgs_IsNoOp()
    {
        var r = new WorkspaceStickyRegistry();
        r.Set("", "ws-1");
        r.Set("/repo", "");
        r.Get("/repo").Should().BeNull();
    }
}
