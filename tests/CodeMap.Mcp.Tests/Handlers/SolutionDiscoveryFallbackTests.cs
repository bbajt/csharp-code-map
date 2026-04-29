namespace CodeMap.Mcp.Tests.Handlers;

using CodeMap.Mcp.Handlers;
using FluentAssertions;

/// <summary>
/// Verifies the project-file fallback added in PHASE-19-01-T04 for
/// <see cref="IndexHandler.DiscoverSolutionPath"/>. Existing .slnx and .sln
/// behaviour is unaffected; the new path activates only when no solution
/// file is present.
/// </summary>
public class SolutionDiscoveryFallbackTests : IDisposable
{
    private readonly string _tempRoot;

    public SolutionDiscoveryFallbackTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "codemap-discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SingleCsprojAtRoot_NoSolution_FallsBackToCsproj()
    {
        var csproj = Path.Combine(_tempRoot, "App.csproj");
        File.WriteAllText(csproj, "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().Be(csproj);
    }

    [Fact]
    public void SingleVbprojAtRoot_NoSolution_FallsBackToVbproj()
    {
        var vbproj = Path.Combine(_tempRoot, "App.vbproj");
        File.WriteAllText(vbproj, "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().Be(vbproj);
    }

    [Fact]
    public void SingleFsprojAtRoot_NoSolution_FallsBackToFsproj()
    {
        var fsproj = Path.Combine(_tempRoot, "App.fsproj");
        File.WriteAllText(fsproj, "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().Be(fsproj);
    }

    [Fact]
    public void TwoProjectsAtRoot_NoSolution_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "A.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_tempRoot, "B.csproj"), "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().BeNull();
    }

    [Fact]
    public void SingleProjectInChildDirectory_FallsBackToChildProject()
    {
        // Mirrors `dotnet new blazor` layout: BlazorTestApp/BlazorTestApp.csproj
        var childDir = Path.Combine(_tempRoot, "MyApp");
        Directory.CreateDirectory(childDir);
        var csproj = Path.Combine(childDir, "MyApp.csproj");
        File.WriteAllText(csproj, "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().Be(csproj);
    }

    [Fact]
    public void MultipleProjectsAcrossChildDirs_ReturnsNull()
    {
        // Multi-project repos are expected to ship with a .sln/.slnx — fail fast.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "A"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "B"));
        File.WriteAllText(Path.Combine(_tempRoot, "A", "A.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_tempRoot, "B", "B.csproj"), "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().BeNull();
    }

    [Fact]
    public void SlnxAtRoot_TakesPrecedenceOverCsproj()
    {
        // Existing precedence preserved.
        var slnx = Path.Combine(_tempRoot, "App.slnx");
        var csproj = Path.Combine(_tempRoot, "App.csproj");
        File.WriteAllText(slnx, "<Solution />");
        File.WriteAllText(csproj, "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().Be(slnx);
    }

    [Fact]
    public void EmptyRoot_NoProjects_ReturnsNull()
    {
        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().BeNull();
    }

    [Fact]
    public void SlnTakesPrecedenceOverChildCsproj()
    {
        // Old behaviour: existing .sln wins even if a child csproj exists.
        var sln = Path.Combine(_tempRoot, "App.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File, Format Version 12.00");

        var childDir = Path.Combine(_tempRoot, "Inner");
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "Inner.csproj"), "<Project />");

        var result = IndexHandler.DiscoverSolutionPath(_tempRoot, providedPath: null);

        result.Should().Be(sln);
    }
}
