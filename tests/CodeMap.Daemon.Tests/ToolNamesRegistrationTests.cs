namespace CodeMap.Daemon.Tests;

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeMap.Core.Models;
using CodeMap.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// The daemon's real tool registry (PHASE-21-09 T01, ADR-051): <c>tools/list</c> shows only the 28
/// MCP-safe canonical names, and every deprecated dotted name still resolves as an alias.
/// </summary>
public sealed class ToolNamesRegistrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ServiceProvider _sp;

    public ToolNamesRegistrationTests()
    {
        Directory.CreateDirectory(_tempDir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCodeMapServices(baseDir: _tempDir);
        _sp = services.BuildServiceProvider();
        ServiceRegistration.RegisterMcpTools(_sp);
    }

    public void Dispose()
    {
        _sp.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ToolsList_AllNamesMatchMcpSafeRegex()
    {
        var names = await ToolsListNamesAsync();

        names.Should().HaveCount(28);
        names.Where(n => !Regex.IsMatch(n, "^[a-zA-Z0-9_-]{1,64}$")).Should().BeEmpty();
    }

    [Fact]
    public async Task ToolsList_ContainsNoAliases_AndEqualsToolNamesAll()
    {
        var names = await ToolsListNamesAsync();

        names.Should().BeEquivalentTo(ToolNames.All);
        names.Should().NotContain(ToolNames.LegacyAliases.Keys);
    }

    [Fact]
    public void Registry_EveryLegacyAlias_ResolvesToItsCanonicalTool()
    {
        var registry = _sp.GetRequiredService<ToolRegistry>();

        foreach (var (alias, canonical) in ToolNames.LegacyAliases)
        {
            var tool = registry.Find(alias, out var usedAlias);
            tool.Should().NotBeNull(alias);
            tool!.Name.Should().Be(canonical);
            usedAlias.Should().Be(alias);
        }
    }

    private async Task<List<string>> ToolsListNamesAsync()
    {
        var server = _sp.GetRequiredService<McpServer>();
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"));
        using var output = new MemoryStream();

        await server.RunAsync(input, output, CancellationToken.None);

        var line = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        return JsonNode.Parse(line)!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();
    }
}
