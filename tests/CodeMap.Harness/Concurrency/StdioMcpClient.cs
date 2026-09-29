namespace CodeMap.Harness.Concurrency;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// One real <c>codemap-mcp</c> child process driven over newline-delimited JSON-RPC on stdio —
/// topology T1 ("every agent spawns its own server"). One outstanding request at a time,
/// like a real agent; concurrent callers (e.g. a peer's cross-talk probe) queue on a gate,
/// and latency is measured from write to response inside the gate.
/// </summary>
public sealed class StdioMcpClient : IMcpClient
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly StreamReader _stdout;
    private readonly Task _stderrPump;
    private readonly TimeSpan _callTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;
    private bool _dead;

    private StdioMcpClient(Process process, Task stderrPump, TimeSpan callTimeout)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput;
        _stderrPump = stderrPump;
        _callTimeout = callTimeout;
        ProcessId = process.Id;
    }

    /// <inheritdoc/>
    public int ProcessId { get; }

    /// <inheritdoc/>
    public TimeSpan InitializeElapsed { get; private set; }

    /// <summary>
    /// Starts <c>dotnet &lt;daemonDll&gt;</c> with the given environment overrides (null value =
    /// remove the variable), pumps stderr to <paramref name="stderrLogPath"/>, and completes the
    /// MCP <c>initialize</c> handshake.
    /// </summary>
    public static async Task<StdioMcpClient> StartAsync(
        string daemonDll,
        IReadOnlyDictionary<string, string?> environment,
        string stderrLogPath,
        TimeSpan callTimeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(daemonDll);
        foreach (var (key, value) in environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        var sw = Stopwatch.StartNew();
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start daemon: dotnet {daemonDll}");

        Directory.CreateDirectory(Path.GetDirectoryName(stderrLogPath)!);
        var stderrFile = new FileStream(stderrLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var pump = PumpAsync(process.StandardError.BaseStream, stderrFile);

        var client = new StdioMcpClient(process, pump, callTimeout);
        try
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        client.InitializeElapsed = sw.Elapsed;
        return client;
    }

    /// <inheritdoc/>
    public async Task<McpCallResult> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_dead)
                return Fail(tool, "TRANSPORT_EXIT", "daemon process is not running", TimeSpan.Zero);

            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_callTimeout);
            var sw = Stopwatch.StartNew();
            JsonNode? response;
            try
            {
                await WriteLineAsync(request, timeout.Token).ConfigureAwait(false);
                response = await ReadResponseAsync(id, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                MarkDead();
                return Fail(tool, "TIMEOUT", $"no response within {_callTimeout.TotalSeconds:0}s", sw.Elapsed);
            }
            catch (IOException ex)
            {
                MarkDead();
                return Fail(tool, "TRANSPORT_EXIT", ex.Message, sw.Elapsed);
            }
            sw.Stop();

            if (response is null)
            {
                MarkDead();
                return Fail(tool, "TRANSPORT_EXIT", $"stdout closed (exit code {SafeExitCode()})", sw.Elapsed);
            }
            return Classify(tool, response, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes stdin, waits briefly for a graceful exit, then kills the process tree.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _stdin.Close(); } catch (IOException) { }

        using (var grace = new CancellationTokenSource(ShutdownGrace))
        {
            try { await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        KillQuietly();

        try { await _stderrPump.ConfigureAwait(false); } catch (IOException) { }
        _process.Dispose();
        _gate.Dispose();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task InitializeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InitializeTimeout);

        var id = Interlocked.Increment(ref _nextId);
        await WriteLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "codemap-harness-concurrency", ["version"] = "1" },
            },
        }, timeout.Token).ConfigureAwait(false);

        var response = await ReadResponseAsync(id, timeout.Token).ConfigureAwait(false);
        if (response?["result"] is null)
            throw new InvalidOperationException(
                $"Daemon initialize failed (exit code {SafeExitCode()}): {response?.ToJsonString() ?? "<stdout closed>"}");

        await WriteLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        }, timeout.Token).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stdin.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> ReadResponseAsync(int id, CancellationToken ct)
    {
        while (true)
        {
            var line = await _stdout.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }

            if (node?["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var got) && got == id)
                return node;
            // Notifications or stale responses — skip.
        }
    }

    private static McpCallResult Classify(string tool, JsonNode response, TimeSpan latency)
    {
        if (response["error"] is JsonObject rpcError)
        {
            var code = rpcError["code"]?.GetValue<int>() ?? 0;
            var message = rpcError["message"]?.GetValue<string>();
            return Fail(tool, code == -32603 ? "INTERNAL" : $"RPC_{code}", message, latency);
        }

        var result = response["result"];
        var text = result?["content"]?[0]?["text"]?.GetValue<string>();
        var isError = result?["isError"]?.GetValue<bool>() ?? false;
        if (!isError)
            return new McpCallResult(tool, true, null, null, latency, text);

        string code2 = "TOOL_ERROR";
        string? message2 = text;
        try
        {
            var err = text is null ? null : JsonNode.Parse(text);
            code2 = err?["code"]?.GetValue<string>() ?? code2;
            message2 = err?["message"]?.GetValue<string>() ?? message2;
        }
        catch (JsonException) { }
        return new McpCallResult(tool, false, code2, message2, latency, text);
    }

    private static McpCallResult Fail(string tool, string code, string? message, TimeSpan latency) =>
        new(tool, false, code, message, latency, null);

    private void MarkDead()
    {
        _dead = true;
        KillQuietly();
    }

    private void KillQuietly()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private string SafeExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode.ToString() : "running"; }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private static async Task PumpAsync(Stream source, FileStream target)
    {
        await using (target.ConfigureAwait(false))
        {
            try { await source.CopyToAsync(target).ConfigureAwait(false); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
