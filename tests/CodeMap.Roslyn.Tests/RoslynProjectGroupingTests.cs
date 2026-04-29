namespace CodeMap.Roslyn.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Pins <see cref="RoslynProjectGrouping"/> behaviour for the M20-01
/// multi-target compilation collapse. Covers TFM parsing, canonical-name
/// stripping, the rank score, and the full grouping over a synthetic
/// <c>Solution</c>.
/// </summary>
public class RoslynProjectGroupingTests
{
    // ── ParseTfm / StripTfm ──────────────────────────────────────────────────

    [Theory]
    [InlineData("MudBlazor(net10.0)",       "net10.0")]
    [InlineData("MyLib(netstandard2.1)",    "netstandard2.1")]
    [InlineData("App(net48)",               "net48")]
    [InlineData("X(net10.0-windows10.0.19041.0)", "net10.0-windows10.0.19041.0")]
    [InlineData("MudBlazor",                null)]
    [InlineData("Some.Project.Name",        null)]
    [InlineData("Has.Dot(In.Name).But(net10.0)",  "net10.0")]   // last paren wins
    [InlineData("App(netcoreapp3.1)",       "netcoreapp3.1")]
    public void ParseTfm_HandlesRealisticInputs(string input, string? expected)
    {
        RoslynProjectGrouping.ParseTfm(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("My (Backup) Lib")]            // not parens-suffix, anyway
    [InlineData("Tooling (legacy)")]           // parens-suffix, but not TFM-shaped
    [InlineData("App (Internal)")]
    [InlineData("Lib (NotATfm)")]              // capital N — TFM regex anchors on net*
    public void ParseTfm_NonTfmParenthetical_ReturnsNull(string input)
    {
        RoslynProjectGrouping.ParseTfm(input).Should().BeNull(
            "regex must require net/netstandard/netcoreapp prefix to avoid mis-classifying user-named projects");
    }

    [Fact]
    public void StripTfm_NonTfmParenthetical_LeavesNameIntact()
    {
        RoslynProjectGrouping.StripTfm("Tooling (legacy)").Should().Be("Tooling (legacy)");
        RoslynProjectGrouping.StripTfm("My (Backup) Lib").Should().Be("My (Backup) Lib");
    }

    [Theory]
    [InlineData("MudBlazor(net10.0)", "MudBlazor")]
    [InlineData("MyLib(netstandard2.1)", "MyLib")]
    [InlineData("Spaced.Name (net8.0)", "Spaced.Name")]
    [InlineData("NoParens", "NoParens")]
    [InlineData("Trailing.Whitespace   (net9.0)", "Trailing.Whitespace")]
    public void StripTfm_RemovesParentheticalAndTrailingWhitespace(string input, string expected)
    {
        RoslynProjectGrouping.StripTfm(input).Should().Be(expected);
    }

    // ── RankTfm ──────────────────────────────────────────────────────────────

    [Fact]
    public void RankTfm_NetCurrent_BeatsNetCoreApp()
    {
        RoslynProjectGrouping.RankTfm("net5.0").Should().BeGreaterThan(
            RoslynProjectGrouping.RankTfm("netcoreapp3.1"));
    }

    [Fact]
    public void RankTfm_NetCoreApp_BeatsNetStandard()
    {
        RoslynProjectGrouping.RankTfm("netcoreapp3.1").Should().BeGreaterThan(
            RoslynProjectGrouping.RankTfm("netstandard2.1"));
    }

    [Fact]
    public void RankTfm_NetStandard_BeatsFramework()
    {
        RoslynProjectGrouping.RankTfm("netstandard2.0").Should().BeGreaterThan(
            RoslynProjectGrouping.RankTfm("net48"));
    }

    [Fact]
    public void RankTfm_HigherNet_BeatsLowerNet()
    {
        RoslynProjectGrouping.RankTfm("net10.0").Should().BeGreaterThan(
            RoslynProjectGrouping.RankTfm("net9.0"));
        RoslynProjectGrouping.RankTfm("net9.0").Should().BeGreaterThan(
            RoslynProjectGrouping.RankTfm("net8.0"));
        RoslynProjectGrouping.RankTfm("net8.0").Should().BeGreaterThan(
            RoslynProjectGrouping.RankTfm("net5.0"));
    }

    [Fact]
    public void RankTfm_OsSuffixedTfm_ParsesPrefixOnly()
    {
        // net10.0-windows... should rank the same as net10.0 for our purposes.
        var win = RoslynProjectGrouping.RankTfm("net10.0-windows10.0.19041.0");
        var bare = RoslynProjectGrouping.RankTfm("net10.0");
        win.Should().Be(bare);
    }

    [Fact]
    public void RankTfm_UnparseableOrNull_ReturnsZero()
    {
        RoslynProjectGrouping.RankTfm(null).Should().Be(0);
        RoslynProjectGrouping.RankTfm("").Should().Be(0);
        RoslynProjectGrouping.RankTfm("totally-bogus").Should().Be(0);
    }

    // ── GroupByFilePath end-to-end ───────────────────────────────────────────

    private static Project MakeProject(string name, string? filePath, AdhocWorkspace ws, ProjectId? sharedId = null)
    {
        var info = ProjectInfo.Create(
            id: sharedId ?? ProjectId.CreateNewId(),
            version: VersionStamp.Default,
            name: name,
            assemblyName: name,
            language: LanguageNames.CSharp,
            filePath: filePath);
        return ws.AddProject(info);
    }

    [Fact]
    public void Group_SingleTargetProject_YieldsGroupOfOne()
    {
        using var ws = new AdhocWorkspace();
        var p = MakeProject("OnlyOne", @"C:\repo\OnlyOne.csproj", ws);

        var groups = RoslynProjectGrouping.GroupByFilePath([p]);

        groups.Should().HaveCount(1);
        var g = groups[0];
        g.CanonicalName.Should().Be("OnlyOne");
        g.AllProjects.Should().ContainSingle();
        g.TargetFrameworks.Should().ContainSingle().Which.Should().Be("(unknown)");
    }

    [Fact]
    public void Group_MultiTargetSameFilePath_CollapsesToOneGroupRankedHighestFirst()
    {
        using var ws = new AdhocWorkspace();
        var p8 = MakeProject("MyLib(net8.0)",  @"C:\repo\MyLib.csproj", ws);
        var p9 = MakeProject("MyLib(net9.0)",  @"C:\repo\MyLib.csproj", ws);
        var p10 = MakeProject("MyLib(net10.0)", @"C:\repo\MyLib.csproj", ws);

        // Feed in a deliberately scrambled order to verify rank-ordering.
        var groups = RoslynProjectGrouping.GroupByFilePath([p9, p8, p10]);

        groups.Should().HaveCount(1);
        var g = groups[0];
        g.CanonicalName.Should().Be("MyLib");
        g.FilePath.Should().Be(@"C:\repo\MyLib.csproj");
        g.AllProjects.Should().HaveCount(3);
        g.TargetFrameworks.Should().Equal(["net10.0", "net9.0", "net8.0"]);
        // First entry is the canonical pick.
        RoslynProjectGrouping.ParseTfm(g.AllProjects[0].Name).Should().Be("net10.0");
    }

    [Fact]
    public void Group_DistinctFilePaths_YieldDistinctGroups()
    {
        using var ws = new AdhocWorkspace();
        var a = MakeProject("LibA(net10.0)", @"C:\repo\LibA.csproj", ws);
        var b = MakeProject("LibB(net10.0)", @"C:\repo\LibB.csproj", ws);

        var groups = RoslynProjectGrouping.GroupByFilePath([a, b]);

        groups.Should().HaveCount(2);
        groups.Select(g => g.CanonicalName).Should().BeEquivalentTo(["LibA", "LibB"]);
    }

    [Fact]
    public void Group_MixedSingleAndMulti_HandledIndependently()
    {
        using var ws = new AdhocWorkspace();
        var lonely = MakeProject("Solo", @"C:\repo\Solo.csproj", ws);
        var multi8 = MakeProject("Multi(net8.0)", @"C:\repo\Multi.csproj", ws);
        var multi10 = MakeProject("Multi(net10.0)", @"C:\repo\Multi.csproj", ws);

        var groups = RoslynProjectGrouping.GroupByFilePath([lonely, multi8, multi10]);

        groups.Should().HaveCount(2);
        var solo = groups.Single(g => g.CanonicalName == "Solo");
        var multi = groups.Single(g => g.CanonicalName == "Multi");
        solo.AllProjects.Should().ContainSingle();
        multi.AllProjects.Should().HaveCount(2);
        multi.TargetFrameworks.Should().Equal(["net10.0", "net8.0"]);
    }

    [Fact]
    public void Group_NullFilePath_TreatedAsSingleton()
    {
        // In-memory test compilations have no FilePath. They must not be merged
        // even if their names happen to collide.
        using var ws = new AdhocWorkspace();
        var p1 = MakeProject("Same(net10.0)", filePath: null, ws);
        var p2 = MakeProject("Same(net10.0)", filePath: null, ws);

        var groups = RoslynProjectGrouping.GroupByFilePath([p1, p2]);

        groups.Should().HaveCount(2,
            "projects without FilePath cannot be collapsed even if names collide");
    }

    [Fact]
    public void Group_FilePathCaseInsensitive_OnAllPlatforms()
    {
        // Projects emitted by MSBuildWorkspace can sometimes vary in case
        // (different references, OS-relative paths). Group by case-insensitive
        // FilePath so they still merge.
        using var ws = new AdhocWorkspace();
        var lower = MakeProject("X(net8.0)",  @"C:\repo\x.csproj", ws);
        var upper = MakeProject("X(net10.0)", @"C:\REPO\X.CSPROJ", ws);

        var groups = RoslynProjectGrouping.GroupByFilePath([lower, upper]);

        groups.Should().HaveCount(1);
    }
}
