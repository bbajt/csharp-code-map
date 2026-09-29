namespace CodeMap.Roslyn.Tests;

using FluentAssertions;
using Xunit;

/// <summary>
/// F12 (PHASE-21-07 T02): finding a project's NuGet restore output (<c>project.assets.json</c>)
/// without evaluating MSBuild. Works in temp dirs only (never the real checkout).
/// </summary>
public sealed class RestoreOutputProbeTests : IDisposable
{
    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string LegacyProject = "<?xml version=\"1.0\"?><Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup><Reference Include=\"System\" /></ItemGroup></Project>";
    private const string LegacyProjectWithPackageReference = "<Project ToolsVersion=\"15.0\"><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-restoreprobe-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Removes the temp tree.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FindMissing_SdkProjectWithAssetsInObj_Null()
    {
        var proj = Project("src/Web/Web.csproj", SdkProject);
        Touch("src/Web/obj/project.assets.json");

        RestoreOutputProbe.FindMissing(proj, Intermediate("src/Web/obj/Debug/net10.0/Web.dll"), _root)
            .Should().BeNull();
    }

    [Fact]
    public void FindMissing_SdkProjectWithoutAssets_ReturnsObjPath()
    {
        var proj = Project("src/Web/Web.csproj", SdkProject);

        RestoreOutputProbe.FindMissing(proj, Intermediate("src/Web/obj/Debug/net10.0/Web.dll"), _root)
            .Should().Be("src/Web/obj/project.assets.json");
    }

    [Fact]
    public void FindMissing_LegacyProjectWithoutPackageReference_Null()
    {
        var proj = Project("Legacy/Legacy.csproj", LegacyProject);

        RestoreOutputProbe.FindMissing(proj, Intermediate("Legacy/obj/Debug/Legacy.dll"), _root)
            .Should().BeNull("packages.config / reference-only projects never have an assets file");
    }

    [Fact]
    public void FindMissing_LegacyProjectWithPackageReferenceWithoutAssets_ReturnsObjPath()
    {
        var proj = Project("Legacy/Legacy.csproj", LegacyProjectWithPackageReference);

        RestoreOutputProbe.FindMissing(proj, Intermediate("Legacy/obj/Debug/Legacy.dll"), _root)
            .Should().Be("Legacy/obj/project.assets.json");
    }

    [Fact]
    public void FindMissing_ArtifactsLayoutAssets_Null()
    {
        // .NET 8+ UseArtifactsOutput: artifacts/obj/<project>/project.assets.json,
        // intermediate assembly at artifacts/obj/<project>/debug/<project>.dll.
        var proj = Project("src/Web/Web.csproj", SdkProject);
        Touch("artifacts/obj/Web/project.assets.json");

        RestoreOutputProbe.FindMissing(proj, Intermediate("artifacts/obj/Web/debug/Web.dll"), _root)
            .Should().BeNull();
    }

    [Fact]
    public void FindMissing_RidSpecificIntermediatePath_WalksUpToAssets()
    {
        var proj = Project("src/Web/Web.csproj", SdkProject);
        Touch("src/Web/obj/project.assets.json");

        RestoreOutputProbe.FindMissing(proj, Intermediate("src/Web/obj/Release/net10.0/win-x64/Web.dll"), _root)
            .Should().BeNull();
    }

    [Fact]
    public void FindMissing_WalkUpNeverLeavesSolutionDir()
    {
        // An assets file above the solution dir belongs to something else and must not count.
        var solutionDir = Path.Combine(_root, "repo");
        var proj = Project("repo/Web.csproj", SdkProject);
        Touch("project.assets.json");

        RestoreOutputProbe.FindMissing(proj, Path.Combine(_root, "repo", "o", "Web.dll"), solutionDir)
            .Should().Be("obj/project.assets.json");
    }

    [Fact]
    public void FindMissing_NoProjectFile_Null()
    {
        RestoreOutputProbe.FindMissing(null, null, _root).Should().BeNull();
        RestoreOutputProbe.FindMissing(Path.Combine(_root, "nope.csproj"), null, _root).Should().BeNull();
    }

    private string Project(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Intermediate(string relative) => Path.Combine(_root, relative);

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
    }
}
