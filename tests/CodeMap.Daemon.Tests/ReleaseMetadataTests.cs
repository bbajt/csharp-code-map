namespace CodeMap.Daemon.Tests;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeMap.Core.Models;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-11 T01: release metadata derived from one source can't drift. The csproj <c>&lt;Version&gt;</c>
/// is the source of truth; <c>server.json</c> (MCP registry entry) and the NuGet description must agree
/// with it and with the real tool surface, and CI must build with the SDK pinned in <c>global.json</c>.
/// </summary>
public sealed class ReleaseMetadataTests
{
    private static readonly string Root = FindRepoRoot();

    private static XDocument Csproj() =>
        XDocument.Load(Path.Combine(Root, "src", "CodeMap.Daemon", "CodeMap.Daemon.csproj"));

    private static string CsprojProperty(string name) =>
        Csproj().Descendants(name).Single().Value;

    [Fact]
    public void ServerJson_Versions_MatchCsprojVersion()
    {
        var version = CsprojProperty("Version");
        var server = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "server.json")))!;

        server["version"]!.GetValue<string>().Should().Be(version, "server.json follows the csproj <Version>");
        server["packages"]!.AsArray()
            .Where(p => p!["registryType"]!.GetValue<string>() == "nuget")
            .Select(p => p!["version"]!.GetValue<string>())
            .Should().ContainSingle().Which.Should().Be(version);
    }

    [Fact]
    public void PackageDescription_ToolCount_MatchesToolNames()
    {
        var description = CsprojProperty("PackageDescription");

        var match = Regex.Match(description, @"(\d+) focused MCP tools");
        match.Success.Should().BeTrue("the description states the tool count");
        int.Parse(match.Groups[1].Value).Should().Be(ToolNames.All.Count);
    }

    [Fact]
    public void PackageDescription_NoSqliteOrEngineSwitch()
    {
        var description = CsprojProperty("PackageDescription");

        description.Should().NotContain("SQLite", "the SQLite engine was removed in v2.1.0");
        description.Should().NotContain("CODEMAP_ENGINE", "nothing reads it");
    }

    [Fact]
    public void CiWorkflow_UsesGlobalJson_NoHardcodedSdk()
    {
        var ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

        ci.Should().Contain("global-json-file: global.json", "the SDK comes from global.json, one source");
        ci.Should().NotContain("dotnet-version:");
        ci.Should().NotMatchRegex(@"dotnet test(?!\S)", "a solution-wide dotnet test can run out of memory; run projects one by one");
    }

    [Fact]
    public void ReleaseWorkflow_Absent()
    {
        File.Exists(Path.Combine(Root, ".github", "workflows", "release.yml")).Should().BeFalse(
            "releases are manual (nuget-push.ps1); a tag pushed to the mirror must never publish");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CodeMap.sln not found above the test output");
    }
}
