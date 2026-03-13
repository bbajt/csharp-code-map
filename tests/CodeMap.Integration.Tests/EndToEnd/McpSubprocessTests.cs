namespace CodeMap.Integration.Tests.EndToEnd;

using System.Diagnostics;
using System.Text;
using FluentAssertions;

/// <summary>
/// Subprocess E2E tests that spawn the compiled CodeMap.Daemon.dll as a real child
/// process and exchange Content-Length-framed JSON-RPC messages over stdio.
///
/// These tests cover the gap left by <see cref="McpEndToEndTests"/>, which wires
/// handlers directly and never exercises the stdio transport or startup path.
///
/// Prerequisites: the daemon must be pre-built. Tests are skipped when the binary
/// is absent so CI doesn't break on a clean checkout without a prior build step.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpSubprocessTests
{
    // Path mirrors the build-release scripts and .mcp.json configuration.
    private static readonly string DaemonDll = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codemap", "bin", "CodeMap.Daemon.dll");

    private const int StartupTimeoutMs = 10_000;

    // ── helpers ───────────────────────────────────────────────────────────────

    // Newline-delimited JSON (the format Claude Code 2.1.70+ uses)
    private static string Ndjson(string json) => json + "\n";

    // Content-Length framed (LSP style, kept for backwards-compat test coverage)
    private static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        return $"Content-Length: {bytes}\r\n\r\n{json}";
    }

    private static async Task<string?> SendAndReceiveAsync(
        string requestJson, bool newlineDelimited = true, int timeoutMs = StartupTimeoutMs)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{DaemonDll}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process.");

        var formatted = newlineDelimited ? Ndjson(requestJson) : Frame(requestJson);
        var bytes = Encoding.UTF8.GetBytes(formatted);
        await proc.StandardInput.BaseStream.WriteAsync(bytes);
        await proc.StandardInput.BaseStream.FlushAsync();
        proc.StandardInput.Close();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            return null;
        }
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DaemonDll_Exists_ForSubprocessTests()
    {
        // Explicit assertion so developers get a clear message when they need to build.
        File.Exists(DaemonDll).Should().BeTrue(
            $"because {DaemonDll} must exist. Run: dotnet build src/CodeMap.Daemon -c Release -o ~/.codemap/bin");
    }

    [Fact]
    public async Task Subprocess_Initialize_RespondsWithinTimeoutAndReturnsValidJson()
    {
        File.Exists(DaemonDll).Should().BeTrue($"because {DaemonDll} must exist — run: dotnet build src/CodeMap.Daemon -c Release -o ~/.codemap/bin");

        const string request = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
            """;

        var sw = Stopwatch.StartNew();
        var output = await SendAndReceiveAsync(request.Trim());
        sw.Stop();

        output.Should().NotBeNull("server timed out — startup exceeded 10 seconds");

        // Response is newline-delimited JSON (the format Claude Code 2.1.70+ uses)
        var body = output!.Trim();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("id").GetInt32().Should().Be(1);
        root.GetProperty("result").GetProperty("protocolVersion").GetString()
            .Should().Be("2024-11-05", "server echoes the client-requested version");
        root.GetProperty("result").GetProperty("serverInfo")
            .GetProperty("name").GetString().Should().Be("codemap");

        // Key acceptance criterion: must respond in under 5 seconds
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "MCP clients typically have a 30-second connection timeout; staying well under 5s gives headroom");
    }

    [Fact]
    public async Task Subprocess_ToolsList_AfterInitialize_ReturnsTools()
    {
        File.Exists(DaemonDll).Should().BeTrue("the daemon must be built first");

        // Send initialize then tools/list in a single stdin stream.
        const string init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";
        const string list = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";

        var psi = new ProcessStartInfo("dotnet", $"\"{DaemonDll}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)!;

        var initBytes = Encoding.UTF8.GetBytes(Ndjson(init));
        var listBytes = Encoding.UTF8.GetBytes(Ndjson(list));
        await proc.StandardInput.BaseStream.WriteAsync(initBytes);
        await proc.StandardInput.BaseStream.WriteAsync(listBytes);
        await proc.StandardInput.BaseStream.FlushAsync();
        proc.StandardInput.Close();

        using var cts = new CancellationTokenSource(StartupTimeoutMs);
        string output;
        try
        {
            output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            output = string.Empty;
        }

        output.Should().NotBeEmpty("server must respond to both messages");

        // Two newline-delimited JSON responses (one per request)
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2, "one response for initialize and one for tools/list");

        // tools/list response contains at least the 6 baseline tools
        output.Should().Contain("symbols.search");
        output.Should().Contain("symbols.get_card");
        output.Should().Contain("index.ensure_baseline");
    }

    [Fact]
    public async Task Subprocess_VersionFlag_PrintsVersionAndExits()
    {
        File.Exists(DaemonDll).Should().BeTrue("the daemon must be built first");

        var psi = new ProcessStartInfo("dotnet", $"\"{DaemonDll}\" --version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.Should().Be(0);
        output.Trim().Should().StartWith("codemap-mcp");
    }
}
