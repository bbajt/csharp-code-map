namespace CodeMap.Core.Tests.Errors;

using CodeMap.Core.Errors;
using FluentAssertions;

public sealed class CodeMapErrorTests
{
    [Fact]
    public void NotFound_CreatesErrorWithNotFoundCode()
    {
        var err = CodeMapError.NotFound("Symbol", "Ns.Class");
        err.Code.Should().Be(ErrorCodes.NotFound);
        err.Message.Should().Contain("Ns.Class");
    }

    [Fact]
    public void InvalidArgument_CreatesErrorWithInvalidArgumentCode()
    {
        var err = CodeMapError.InvalidArgument("bad param");
        err.Code.Should().Be(ErrorCodes.InvalidArgument);
        err.Message.Should().Be("bad param");
    }

    [Fact]
    public void BudgetExceeded_IncludesDetailsWithLimitInfo()
    {
        var err = CodeMapError.BudgetExceeded("MaxResults", 200, 100);
        err.Code.Should().Be(ErrorCodes.BudgetExceeded);
        err.Details.Should().ContainKey("requested");
        err.Details.Should().ContainKey("hard_cap");
        err.Details!["requested"].Should().Be(200);
        err.Details["hard_cap"].Should().Be(100);
    }

    [Fact]
    public void IndexNotAvailable_IsRetryable()
    {
        var err = CodeMapError.IndexNotAvailable("repo-1", "aabbccddee00112233445566778899aabbccddee");
        err.Code.Should().Be(ErrorCodes.IndexNotAvailable);
        err.Retryable.Should().BeTrue();
    }

    [Fact]
    public void CompilationFailed_IsRetryable()
    {
        var err = CodeMapError.CompilationFailed("build failed");
        err.Code.Should().Be(ErrorCodes.CompilationFailed);
        err.Retryable.Should().BeTrue();
    }

    [Fact]
    public void CompilationFailed_WithFailedProjects_IncludesInDetails()
    {
        var err = CodeMapError.CompilationFailed("failed", ["ProjectA", "ProjectB"]);
        err.Details.Should().ContainKey("failed_projects");
    }

    [Fact]
    public void CompilationFailed_NoProjects_DetailsIsNull()
    {
        var err = CodeMapError.CompilationFailed("failed");
        err.Details.Should().BeNull();
    }
}
