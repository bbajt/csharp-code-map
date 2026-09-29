namespace CodeMap.Integration.Tests.Concurrency;

using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>
/// Tests for <see cref="StdioMcpClient"/> against the build-under-test daemon, isolated
/// under a temporary <c>CODEMAP_HOME</c> (PHASE-21-01 T02).
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class StdioMcpClientTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-stdio-client-" + Path.GetRandomFileName());
    private StdioMcpClient _client = null!;

    /// <summary>Starts one daemon with a temp data root.</summary>
    public async ValueTask InitializeAsync()
    {
        _client = await StdioMcpClient.StartAsync(
            ConcurrencyOptions.DefaultDaemonPath(),
            new Dictionary<string, string?>
            {
                ["CODEMAP_HOME"] = Path.Combine(_root, "home"),
                ["CODEMAP_CACHE_DIR"] = null,
            },
            Path.Combine(_root, "stderr.log"),
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Stops the daemon and removes the temp root.</summary>
    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        AgentScratchRepo.DeleteDirectoryRobust(_root);
    }

    [Fact]
    public async Task CallToolAsync_Guide_Roundtrips()
    {
        var result = await _client.CallToolAsync("codemap_guide", new JsonObject(), TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue(result.ErrorMessage);
        result.Payload.Should().NotBeNullOrWhiteSpace();
        result.Latency.Should().BePositive();
        _client.InitializeElapsed.Should().BePositive();
    }

    [Fact]
    public async Task CallToolAsync_UnknownTool_ReturnsFailureWithCode()
    {
        var result = await _client.CallToolAsync("no.such_tool", new JsonObject(), TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CallToolAsync_AfterDaemonKilled_ReturnsTransportExit()
    {
        using (var p = Process.GetProcessById(_client.ProcessId))
        {
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var first = await _client.CallToolAsync("codemap_guide", new JsonObject(), TestContext.Current.CancellationToken);
        var second = await _client.CallToolAsync("codemap_guide", new JsonObject(), TestContext.Current.CancellationToken);

        first.ErrorCode.Should().Be("TRANSPORT_EXIT");
        second.ErrorCode.Should().Be("TRANSPORT_EXIT");
    }
}
