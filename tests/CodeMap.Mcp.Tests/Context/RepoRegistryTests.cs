namespace CodeMap.Mcp.Tests.Context;

using CodeMap.Core.Errors;
using CodeMap.Mcp.Context;
using FluentAssertions;

public sealed class RepoRegistryTests
{
    [Fact]
    public void Resolve_ExplicitPath_ReturnsVerbatim()
    {
        // Explicit path wins even when registry is empty — preserves the precise caller path.
        var r = new RepoRegistry();
        var result = r.Resolve("/some/repo");
        result.IsSuccess.Should().BeTrue();
        result.RepoPath.Should().Be("/some/repo");
    }

    [Fact]
    public void Resolve_NoRepos_ReturnsError()
    {
        var r = new RepoRegistry();
        var result = r.Resolve(null);
        result.RepoPath.Should().BeNull();
        result.Error!.Code.Should().Be(ErrorCodes.InvalidArgument);
        result.Error!.Message.Should().Contain("no repo has been indexed");
    }

    [Fact]
    public void Resolve_SingleRepo_ReturnsThatRepo()
    {
        var r = new RepoRegistry();
        r.Register("/path/to/repo");
        var result = r.Resolve(null);
        result.IsSuccess.Should().BeTrue();
        // Stored form is normalized (absolute + forward slashes); on non-Windows this may
        // differ from the input. Only the suffix is guaranteed portable.
        result.RepoPath.Should().EndWith("/repo");
    }

    [Fact]
    public void Resolve_MultipleRepos_ReturnsErrorWithListing()
    {
        var r = new RepoRegistry();
        r.Register("/repo-a");
        r.Register("/repo-b");
        var result = r.Resolve(null);
        result.RepoPath.Should().BeNull();
        result.Error!.Code.Should().Be(ErrorCodes.InvalidArgument);
        result.Error!.Message.Should().Contain("2 repos are indexed");
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        // Double-register of the same path must not produce two entries.
        var r = new RepoRegistry();
        r.Register("/x");
        r.Register("/x");
        r.KnownRepos.Count.Should().Be(1);
    }

    [Fact]
    public void Register_NormalizesPathVariants_ToSameEntry()
    {
        // "./Foo", "Foo", and "Foo/" should all collapse to one entry.
        var r = new RepoRegistry();
        var cwd = Directory.GetCurrentDirectory();
        r.Register(Path.Combine(cwd, "Foo"));
        r.Register(Path.Combine(cwd, "Foo") + "/");
        r.Register(Path.Combine(cwd, "./Foo"));
        r.KnownRepos.Count.Should().Be(1);
    }

    [Fact]
    public void Forget_RemovesRepoFromResolveDefault()
    {
        var r = new RepoRegistry();
        r.Register("/repo");
        r.Forget("/repo");
        // Default resolve now errors, as if the repo had never been registered.
        var result = r.Resolve(null);
        result.Error!.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public void Forget_UnknownPath_IsNoOp()
    {
        var r = new RepoRegistry();
        r.Forget("/never-registered");
        r.KnownRepos.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ExplicitWins_EvenWithMultipleRegistered()
    {
        var r = new RepoRegistry();
        r.Register("/a");
        r.Register("/b");
        var result = r.Resolve("/c");
        result.IsSuccess.Should().BeTrue();
        result.RepoPath.Should().Be("/c");
    }
}
