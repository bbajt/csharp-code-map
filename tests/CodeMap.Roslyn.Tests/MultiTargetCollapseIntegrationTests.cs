namespace CodeMap.Roslyn.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// End-to-end integration test for the M20-01 multi-target compilation collapse.
/// Materialises a minimal multi-targeted <c>.csproj</c> on disk, runs
/// <see cref="RoslynCompiler.CompileAndExtractAsync"/>, and asserts the
/// resulting <see cref="Core.Models.IndexStats.ProjectDiagnostics"/> reports
/// exactly one row per <c>.csproj</c> with all TFMs aggregated into
/// <c>TargetFrameworks</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MultiTargetCollapseIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MultiTargetCollapseIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-mtc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task MultiTargetProject_CollapsesToSingleProjectDiagnostic_WithAllTfmsListed()
    {
        // Minimal multi-targeted library. Two TFMs both available in 10.0.203
        // SDK by default (net9.0 + net10.0 reference packs ship with the SDK).
        const string csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;
        const string libContent = """
            namespace MultiLib;
            public class Greeter
            {
                public string Hello() => "hi";
            }
            """;
        const string slnContent = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "MultiLib", "MultiLib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                EndGlobalSection
            EndGlobal
            """;

        WriteFile("MultiLib.csproj", csprojContent);
        WriteFile("Greeter.cs", libContent);
        var slnPath = WriteFile("MultiLib.sln", slnContent);

        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var result = await compiler.CompileAndExtractAsync(slnPath);

        // M20-01: exactly one ProjectDiagnostic per .csproj despite 2 TFMs
        result.Stats.ProjectDiagnostics.Should().NotBeNull();
        result.Stats.ProjectDiagnostics!.Should().HaveCount(1,
            "the multi-targeted .csproj must collapse to a single diagnostic");

        var diag = result.Stats.ProjectDiagnostics![0];

        // Canonical name has no TFM parenthetical
        diag.ProjectName.Should().Be("MultiLib");
        diag.ProjectName.Should().NotContain("(", "TFM parenthetical must be stripped");

        // TargetFrameworks lists every TFM declared, ranked highest first
        diag.TargetFrameworks.Should().NotBeNull();
        diag.TargetFrameworks!.Should().Equal(["net10.0", "net9.0"]);

        // Symbols come from the canonical (highest) TFM. Greeter must surface.
        result.Symbols.Should().Contain(s => s.FullyQualifiedName.Contains("MultiLib.Greeter"));
    }

    [Fact]
    public async Task SingleTargetProject_TargetFrameworks_IsNullForWireCompat()
    {
        // Sanity check: single-target projects must keep TargetFrameworks=null
        // so the response shape is unchanged for the non-multi-target case.
        const string csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;
        const string libContent = "namespace SoloLib; public class Foo { }";
        const string slnContent = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "SoloLib", "SoloLib.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                EndGlobalSection
            EndGlobal
            """;

        WriteFile("SoloLib.csproj", csprojContent);
        WriteFile("Foo.cs", libContent);
        var slnPath = WriteFile("SoloLib.sln", slnContent);

        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var result = await compiler.CompileAndExtractAsync(slnPath);

        var diag = result.Stats.ProjectDiagnostics!.Single();
        diag.ProjectName.Should().Be("SoloLib");
        diag.TargetFrameworks.Should().BeNull(
            "single-target projects keep the wire shape from before M20-01");
    }
}
