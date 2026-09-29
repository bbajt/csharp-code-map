namespace CodeMap.Daemon.Tests;

using CodeMap.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Smoke test for the MCP 2025-03-26 <c>annotations</c> rollout: every tool
/// the daemon registers must carry a <see cref="ToolAnnotations"/>. A missing
/// annotation forces compliant clients (Claude Desktop and similar) into
/// their conservative "ask the user" default, defeating the auto-approval
/// flow that makes CodeMap pleasant to use in long sessions. This test
/// regresses if a future tool registration forgets the annotation arg.
/// </summary>
public class ToolAnnotationsCoverageTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ToolAnnotationsCoverageTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void EveryRegisteredTool_HasAnnotations()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCodeMapServices(baseDir: _tempDir);
        using var sp = services.BuildServiceProvider();

        ServiceRegistration.RegisterMcpTools(sp);

        var registry = sp.GetRequiredService<ToolRegistry>();
        var all = registry.GetAll();

        all.Should().HaveCount(28, "CodeMap currently ships 28 MCP tools");
        var unannotated = all
            .Where(t => t.Annotations is null)
            .Select(t => t.Name)
            .ToList();
        unannotated.Should().BeEmpty(
            "every shipped tool must carry annotations — missing entries fall back to client-side "
            + $"conservative defaults. Unannotated: {string.Join(", ", unannotated)}");
    }

    [Fact]
    public void DestructiveTools_AreExactlyTheExpectedSet()
    {
        // Pinning the destructive set explicitly: if a tool ever gets accidentally
        // promoted/demoted from destruct, this test surfaces it BEFORE clients
        // start auto-approving something they shouldn't.
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCodeMapServices(baseDir: _tempDir);
        using var sp = services.BuildServiceProvider();

        ServiceRegistration.RegisterMcpTools(sp);

        var destructive = sp.GetRequiredService<ToolRegistry>().GetAll()
            .Where(t => t.Annotations?.Destructive == true)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        destructive.Should().BeEquivalentTo(new[]
        {
            "index_cleanup",       // purges old baselines
            "index_remove_repo",   // purges all repo data
            "workspace_delete",    // purges workspace overlay
            "workspace_reset",     // discards overlay revisions — irreversible
        }, "these four are the only tools that may cause data loss; flag any change here");
    }
}
