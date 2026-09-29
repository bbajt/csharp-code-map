namespace CodeMap.Integration.Tests.EndToEnd;

using System.Diagnostics;
using System.Text;
using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>
/// Subprocess E2E tests that spawn the compiled CodeMap.Daemon.dll as a real child
/// process and exchange Content-Length-framed JSON-RPC messages over stdio.
///
/// These tests cover the gap left by <see cref="McpEndToEndTests"/>, which wires
/// handlers directly and never exercises the stdio transport or startup path.
///
/// The daemon is the one built with this project (ProjectReference, test output directory), and
/// every spawn runs with a per-test <c>CODEMAP_HOME</c> and no <c>CODEMAP_CACHE_DIR</c>. Until
/// PHASE-21-07 these tests ran the installed <c>~/.codemap/bin</c> copy against the live data root.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpSubprocessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-subprocess-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Removes the per-test data root (best-effort).</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // The daemon built with this test project (ProjectReference), never the installed ~/.codemap/bin
    // copy: that one is whatever version is live, not the code under test.
    private static readonly string DaemonDll = ConcurrencyOptions.DefaultDaemonPath();

    private const int StartupTimeoutMs = 10_000;

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the build-output daemon with a per-test <c>CODEMAP_HOME</c> and no shared cache, so
    /// nothing it writes (logs, <c>_savings.json</c>) reaches the developer's live <c>~/.codemap</c>.
    /// </summary>
    private ProcessStartInfo DaemonStartInfo(string extraArgs = "")
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{DaemonDll}\" {extraArgs}".TrimEnd())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["CODEMAP_HOME"] = Path.Combine(_root, "home");
        psi.Environment.Remove("CODEMAP_CACHE_DIR");
        return psi;
    }

    // Newline-delimited JSON (the format Claude Code 2.1.70+ uses)
    private static string Ndjson(string json) => json + "\n";

    // Content-Length framed (LSP style, kept for backwards-compat test coverage)
    private static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        return $"Content-Length: {bytes}\r\n\r\n{json}";
    }

    private async Task<string?> SendAndReceiveAsync(
        string requestJson, bool newlineDelimited = true, int timeoutMs = StartupTimeoutMs)
    {
        var psi = DaemonStartInfo();

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
    public async Task Subprocess_WritesOnlyIntoItsOwnDataRoot()
    {
        // The daemon a test spawns must never use the developer's live ~/.codemap (logs,
        // _savings.json, store): it runs with a per-test CODEMAP_HOME (PHASE-21-07 follow-up).
        var home = Path.Combine(_root, "home");
        const string request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";

        var output = await SendAndReceiveAsync(request);

        output.Should().NotBeNull();
        Directory.Exists(home).Should().BeTrue("the spawned daemon must use the test's CODEMAP_HOME");
        Directory.EnumerateFileSystemEntries(home, "*", SearchOption.AllDirectories).Should().NotBeEmpty(
            "logs / _savings.json must land in the test's data root, not in ~/.codemap");
    }

    [Fact]
    public void DaemonDll_Exists_ForSubprocessTests()
    {
        // Explicit assertion so developers get a clear message when they need to build.
        File.Exists(DaemonDll).Should().BeTrue(
            $"because {DaemonDll} must exist (built with this test project via its ProjectReference)");
    }

    [Fact]
    public async Task Subprocess_Initialize_RespondsWithinTimeoutAndReturnsValidJson()
    {
        File.Exists(DaemonDll).Should().BeTrue($"because {DaemonDll} must exist (built with this test project via its ProjectReference)");

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

        var psi = DaemonStartInfo();

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

        var psi = DaemonStartInfo("--version");

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.Should().Be(0);
        output.Trim().Should().StartWith("codemap-mcp");
    }
}
