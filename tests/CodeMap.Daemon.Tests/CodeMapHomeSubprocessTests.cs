namespace CodeMap.Daemon.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

/// <summary>
/// Subprocess tests for the <c>CODEMAP_HOME</c> data-root override (PHASE-21-01 T01).
/// Runs the build under test — the <c>CodeMap.Daemon.dll</c> copied into this test
/// project's output by its project reference — never the installed <c>~/.codemap/bin</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CodeMapHomeSubprocessTests : IDisposable
{
    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"codemap-home-test","version":"1"}}}""";

    private static readonly string DaemonDll = Path.Combine(AppContext.BaseDirectory, "CodeMap.Daemon.dll");

    private readonly string _home = Path.Combine(Path.GetTempPath(), "codemap-home-" + Path.GetRandomFileName());

    /// <summary>Removes the temporary data root.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task Daemon_WithCodemapHome_ReadsConfigAndWritesLogsUnderHome()
    {
        Directory.CreateDirectory(_home);
        await File.WriteAllTextAsync(Path.Combine(_home, "config.json"), """{"log_level":"Debug"}""",
            TestContext.Current.CancellationToken);

        var run = await RunDaemonAsync(_home, InitializeRequest);

        run.ExitCode.Should().Be(0, run.Stderr);
        JsonNode.Parse(run.Stdout.Trim())!["result"]!["serverInfo"]!["name"]!
            .GetValue<string>().Should().Be("codemap");

        // Debug-level "MCP ←" lines only appear if config.json was read from CODEMAP_HOME.
        var logs = Directory.GetFiles(Path.Combine(_home, "logs"), "codemap-*.log");
        logs.Should().ContainSingle();
        (await File.ReadAllTextAsync(logs[0], TestContext.Current.CancellationToken))
            .Should().Contain("MCP");
    }

    [Fact]
    public async Task Daemon_WithCodemapHome_PersistsSavingsUnderHome()
    {
        var run = await RunDaemonAsync(_home, InitializeRequest);

        run.ExitCode.Should().Be(0, run.Stderr);
        // _savings.json is written by AddCodeMapServices' tracker on ProcessExit —
        // proves the service-side baseDir follows CODEMAP_HOME, not just Program.Main.
        File.Exists(Path.Combine(_home, "_savings.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Daemon_WithRelativeCodemapHome_ExitsWithCode2AndStderrMessage()
    {
        var run = await RunDaemonAsync("relative-codemap-home", InitializeRequest);

        run.ExitCode.Should().Be(2);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Contain("CODEMAP_HOME").And.Contain("relative-codemap-home");
    }

    // ── CODEMAP_CACHE_DIR (F11) ───────────────────────────────────────────────

    [Fact]
    public async Task Daemon_WithRelativeCacheDir_ExitsWithCode2AndStderrMessage()
    {
        var run = await RunDaemonAsync(_home, InitializeRequest, cacheDir: "relative-cache");

        run.ExitCode.Should().Be(2, "a relative cache dir would resolve against the client's working directory");
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Contain("CODEMAP_CACHE_DIR").And.Contain("relative-cache");
    }

    [Fact]
    public async Task Daemon_WithBlankCacheDir_IndexesWithoutWritingIntoWorkingDirectory()
    {
        // F11: CODEMAP_CACHE_DIR="" (e.g. an MCP client config with an empty value) was treated as
        // an enabled cache at a relative path, so ensure_baseline pushed the baseline into the
        // daemon's working directory — usually the user's repo root.
        var workDir = Path.Combine(_home, "cwd");
        Directory.CreateDirectory(workDir);
        var repoRoot = FindRepoRoot();
        var solution = Path.Combine(repoRoot, "testdata", "SampleSolution", "SampleSolution.sln");
        var ensureBaseline = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index.ensure_baseline",
                ["arguments"] = new JsonObject { ["repo_path"] = repoRoot, ["solution_path"] = solution },
            },
        }.ToJsonString();

        var run = await RunDaemonAsync(Path.Combine(_home, "home"), InitializeRequest + "\n" + ensureBaseline,
            cacheDir: "", workingDirectory: workDir, timeout: TimeSpan.FromMinutes(3));

        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Contain("\"id\":2").And.NotContain("\"isError\":true");
        Directory.EnumerateFileSystemEntries(workDir).Should().BeEmpty(
            "a blank CODEMAP_CACHE_DIR means no shared cache — nothing may be written to the working directory");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CodeMap.sln not found above the test output");
    }

    private sealed record DaemonRun(int ExitCode, string Stdout, string Stderr);

    /// <param name="cacheDir">Value for CODEMAP_CACHE_DIR; <c>null</c> removes it (the default).</param>
    private static async Task<DaemonRun> RunDaemonAsync(string codemapHome, string requestJson,
        string? cacheDir = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        File.Exists(DaemonDll).Should().BeTrue($"daemon build output expected at {DaemonDll}");

        var psi = new ProcessStartInfo("dotnet", $"\"{DaemonDll}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        psi.Environment["CODEMAP_HOME"] = codemapHome;
        if (cacheDir is null) psi.Environment.Remove("CODEMAP_CACHE_DIR");
        else psi.Environment["CODEMAP_CACHE_DIR"] = cacheDir;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await proc.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(requestJson + "\n"), cts.Token);
            await proc.StandardInput.BaseStream.FlushAsync(cts.Token);
        }
        catch (IOException)
        {
            // Process already exited (e.g. invalid CODEMAP_HOME) — expected for the failure test.
        }
        proc.StandardInput.Close();

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            throw;
        }

        return new DaemonRun(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
