namespace CodeMap.Harness.Concurrency;

using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
/// One simulated agent: setup (<c>ensure_baseline</c> → <c>workspace_create</c> → symbol pool),
/// a seeded read/heavy-read/mutation loop, and teardown. Mutations append a uniquely named
/// class to the agent's edit file, refresh the overlay, and verify the agent sees its own
/// edit; in isolated mode one peer is probed to verify it does <b>not</b> (no cross-talk).
/// </summary>
public sealed class AgentWorkload
{
    private const double LightReadShare = 0.60;
    private const double HeavyReadShare = 0.20;
    private const int BaselineAttempts = 3;
    private static readonly string[] TypeKinds = ["class", "interface", "record", "struct", "enum"];

    private readonly Random _rng;
    private readonly WorkspaceMode _mode;
    private readonly Lock _editLock;
    private readonly IReadOnlyList<string> _queryInputs;
    private readonly List<string> _methods = [];
    private readonly List<string> _types = [];
    private readonly List<string> _anySymbols = [];
    private int _mutationSeq;

    /// <summary>Creates the workload; call <see cref="SetupAsync"/> then <see cref="RunLoopAsync"/>.</summary>
    public AgentWorkload(
        int index,
        IMcpClient client,
        string repoPath,
        string solutionPath,
        string workspaceId,
        string editFile,
        int seed,
        WorkspaceMode mode,
        Lock editLock,
        IReadOnlyList<string> queryInputs)
    {
        Index = index;
        Client = client;
        RepoPath = repoPath;
        SolutionPath = solutionPath;
        WorkspaceId = workspaceId;
        EditFile = editFile;
        _rng = new Random(seed + index);
        _mode = mode;
        _editLock = editLock;
        _queryInputs = queryInputs;
    }

    /// <summary>Agent index (0-based).</summary>
    public int Index { get; }

    /// <summary>The agent's MCP client.</summary>
    public IMcpClient Client { get; }

    /// <summary>Worktree root passed as <c>repo_path</c>.</summary>
    public string RepoPath { get; }

    /// <summary>Solution inside the worktree.</summary>
    public string SolutionPath { get; }

    /// <summary>Workspace id used for every workspace-scoped call.</summary>
    public string WorkspaceId { get; }

    /// <summary>Absolute path of the file mutations append to.</summary>
    public string EditFile { get; }

    /// <summary>All agents of the run (including this one) — for peer probes.</summary>
    public IReadOnlyList<AgentWorkload> Peers { get; set; } = [];

    /// <summary>Accumulated counters; owned by this agent's task.</summary>
    public AgentStats Stats { get; } = new();

    /// <summary>
    /// Waits for <paramref name="startGate"/> (so all agents hit <c>ensure_baseline</c> together),
    /// then indexes, creates the workspace and builds the symbol pool. Returns false if the
    /// agent cannot proceed (recorded in <see cref="AgentStats.SetupFailed"/>).
    /// </summary>
    public async Task<bool> SetupAsync(Task startGate, CancellationToken ct)
    {
        await startGate.WaitAsync(ct).ConfigureAwait(false);

        McpCallResult? baseline = null;
        for (var attempt = 0; attempt < BaselineAttempts; attempt++)
        {
            Stats.BaselineRequests++;
            baseline = await CallAsync("index_ensure_baseline", new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["solution_path"] = SolutionPath,
            }, ct).ConfigureAwait(false);
            if (baseline.Ok) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        if (baseline is not { Ok: true }) return SetupFailed();
        if (baseline.PayloadJson()?["already_existed"]?.GetValue<bool>() == false)
            Stats.BaselineBuilds++;

        var ws = await CallAsync("workspace_create", new JsonObject
        {
            ["workspace_id"] = WorkspaceId,
            ["repo_path"] = RepoPath,
            ["solution_path"] = SolutionPath,
        }, ct).ConfigureAwait(false);
        if (!ws.Ok) return SetupFailed();

        foreach (var input in _queryInputs)
        {
            var search = await CallAsync("symbols_search", WsArgs(new JsonObject
            {
                ["query"] = input,
                ["limit"] = 20,
            }), ct).ConfigureAwait(false);
            foreach (var hit in Hits(search))
            {
                var id = hit["symbol_id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                var kind = hit["kind"]?.GetValue<string>() ?? "";
                _anySymbols.Add(id);
                if (kind == "method") _methods.Add(id);
                else if (TypeKinds.Contains(kind)) _types.Add(id);
            }
        }

        // Inputs that only match types (eShopOnWeb: "Order", "Basket") leave no methods, and the
        // method-only tools would then target types → INVALID_ARGUMENT, skewing error counts and
        // latencies (PHASE-21-06 T01). Ask for methods explicitly.
        if (_methods.Count == 0)
        {
            foreach (var input in _queryInputs)
            {
                var search = await CallAsync("symbols_search", WsArgs(new JsonObject
                {
                    ["query"] = input,
                    ["kinds"] = new JsonArray("method"),
                    ["limit"] = 20,
                }), ct).ConfigureAwait(false);
                foreach (var hit in Hits(search))
                {
                    var id = hit["symbol_id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id) || hit["kind"]?.GetValue<string>() != "method") continue;
                    _anySymbols.Add(id);
                    _methods.Add(id);
                }
            }
        }
        return _anySymbols.Count > 0 || SetupFailed();
    }

    /// <summary>True once <see cref="SetupAsync"/> succeeded; peers only probe ready agents.</summary>
    public bool IsReady => !Stats.SetupFailed && _anySymbols.Count > 0;

    /// <summary>Runs the seeded operation mix until <paramref name="duration"/> elapses.</summary>
    public async Task RunLoopAsync(TimeSpan duration, CancellationToken ct)
    {
        var callsBefore = Stats.Calls;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration && !ct.IsCancellationRequested)
        {
            var roll = _rng.NextDouble();
            if (roll < LightReadShare) await LightReadAsync(ct).ConfigureAwait(false);
            else if (roll < LightReadShare + HeavyReadShare) await HeavyReadAsync(ct).ConfigureAwait(false);
            else await MutateAsync(ct).ConfigureAwait(false);
        }
        Stats.LoopCalls = Stats.Calls - callsBefore;
    }

    /// <summary>Deletes the agent's workspace (best-effort; errors are recorded).</summary>
    public async Task TeardownAsync(CancellationToken ct) =>
        await CallAsync("workspace_delete", new JsonObject
        {
            ["workspace_id"] = WorkspaceId,
            ["repo_path"] = RepoPath,
        }, ct).ConfigureAwait(false);

    // ── Operations ────────────────────────────────────────────────────────────

    private Task<McpCallResult> LightReadAsync(CancellationToken ct) => _rng.Next(5) switch
    {
        0 => CallAsync("symbols_search", WsArgs(new JsonObject { ["query"] = Pick(_queryInputs), ["limit"] = 20 }), ct),
        1 => CallAsync("symbols_get_card", WsArgs(new JsonObject { ["symbol_id"] = Pick(_anySymbols) }), ct),
        2 => CallAsync("refs_find", WsArgs(new JsonObject { ["symbol_id"] = Pick(_anySymbols) }), ct),
        3 => CallAsync("graph_callers", WsArgs(new JsonObject { ["symbol_id"] = PickMethod() }), ct),
        _ => CallAsync("types_hierarchy", WsArgs(new JsonObject { ["symbol_id"] = PickType() }), ct),
    };

    private Task<McpCallResult> HeavyReadAsync(CancellationToken ct) => _rng.Next(3) switch
    {
        0 => CallAsync("graph_trace_feature", WsArgs(new JsonObject { ["entry_point"] = PickMethod(), ["depth"] = 3 }), ct),
        1 => CallAsync("symbols_get_context", WsArgs(new JsonObject { ["symbol_id"] = PickMethod() }), ct),
        _ => CallAsync("codemap_summarize", WsArgs(new JsonObject()), ct),
    };

    private async Task MutateAsync(CancellationToken ct)
    {
        var seq = ++_mutationSeq;
        var name = $"CmBenchA{Index}M{seq}";
        lock (_editLock)
        {
            File.AppendAllText(EditFile,
                $"\ninternal static class {name}\n{{\n    public static int Ping() => {seq};\n}}\n");
        }
        Stats.Mutations++;

        var refresh = await CallAsync("index_refresh_overlay", WsArgs(new JsonObject()), ct).ConfigureAwait(false);
        if (!refresh.Ok)
        {
            Stats.OwnEditSkippedRefreshFailed++;
            return;
        }

        Stats.OwnEditChecks++;
        var own = await CallAsync("symbols_search", WsArgs(new JsonObject { ["query"] = name }), ct).ConfigureAwait(false);
        if (own.Ok && !ContainsSymbol(own, name))
        {
            Stats.OwnEditMissing++;
            Stats.AddSample($"OWN_EDIT_MISSING agent-{Index} {name}");
        }

        // Probe the next *ready* peer; agents whose setup failed have no workspace to probe.
        var peer = Enumerable.Range(1, Math.Max(0, Peers.Count - 1))
            .Select(offset => Peers[(Index + offset) % Peers.Count])
            .FirstOrDefault(p => p.IsReady);
        if (peer is null) return;
        var probe = await peer.Client.CallToolAsync("symbols_search", new JsonObject
        {
            ["query"] = name,
            ["repo_path"] = peer.RepoPath,
            ["workspace_id"] = peer.WorkspaceId,
        }, ct).ConfigureAwait(false);
        Stats.RecordProbe(probe);
        if (!probe.Ok) return;

        var peerSaw = ContainsSymbol(probe, name);
        if (_mode == WorkspaceMode.Isolated)
        {
            Stats.CrossTalkProbes++;
            if (peerSaw)
            {
                Stats.CrossTalkViolations++;
                Stats.AddSample($"CROSS_TALK agent-{peer.Index} saw {name} from agent-{Index}");
            }
        }
        else
        {
            Stats.SharedPeerProbes++;
            if (peerSaw) Stats.SharedPeerSawEdit++;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<McpCallResult> CallAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var result = await Client.CallToolAsync(tool, args, ct).ConfigureAwait(false);
        Stats.Record(result);
        if (result.Ok && Stats.FirstSuccessMs is null)
            Stats.FirstSuccessMs = (Client.InitializeElapsed + result.Latency).TotalMilliseconds;
        return result;
    }

    private JsonObject WsArgs(JsonObject args)
    {
        args["repo_path"] = RepoPath;
        args["workspace_id"] = WorkspaceId;
        return args;
    }

    private bool SetupFailed()
    {
        Stats.SetupFailed = true;
        return false;
    }

    private string Pick(IReadOnlyList<string> items) => items[_rng.Next(items.Count)];

    private string PickMethod() => Pick(_methods.Count > 0 ? _methods : _anySymbols);

    private string PickType() => Pick(_types.Count > 0 ? _types : _anySymbols);

    private static IEnumerable<JsonNode> Hits(McpCallResult result) =>
        result.PayloadJson()?["data"]?["hits"]?.AsArray().OfType<JsonNode>() ?? [];

    private static bool ContainsSymbol(McpCallResult result, string name) =>
        Hits(result).Any(h =>
            (h["symbol_id"]?.GetValue<string>() ?? "").Contains(name, StringComparison.Ordinal) ||
            (h["fully_qualified_name"]?.GetValue<string>() ?? "").Contains(name, StringComparison.Ordinal));
}

/// <summary>
/// Mutable per-agent accumulator. Each instance is written only by its owning agent's task;
/// the runner reads it after all agents finish.
/// </summary>
public sealed class AgentStats
{
    private const int MaxSamples = 20;

    /// <summary>Latencies (ms) per tool, own calls only.</summary>
    public Dictionary<string, List<double>> LatenciesMs { get; } = [];

    /// <summary>Error count per tool, own calls only.</summary>
    public Dictionary<string, int> ToolErrors { get; } = [];

    /// <summary>Error count per code, own calls and probes.</summary>
    public Dictionary<string, int> ErrorsByCode { get; } = [];

    /// <summary>First distinct error/violation descriptions (bounded).</summary>
    public List<string> SampleErrors { get; } = [];

    /// <summary>Own tool calls made (probes excluded), all phases.</summary>
    public int Calls { get; private set; }

    /// <summary>Own tool calls made during the timed loop only (throughput basis).</summary>
    public int LoopCalls { get; set; }

    /// <summary>Initialize time + latency of the first successful call, in ms.</summary>
    public double? FirstSuccessMs { get; set; }

    /// <summary><c>ensure_baseline</c> attempts.</summary>
    public int BaselineRequests { get; set; }

    /// <summary><c>ensure_baseline</c> responses with <c>already_existed: false</c>.</summary>
    public int BaselineBuilds { get; set; }

    /// <summary>True when setup could not complete.</summary>
    public bool SetupFailed { get; set; }

    /// <summary>Mutations performed.</summary>
    public int Mutations { get; set; }

    /// <summary>Own-edit visibility checks performed.</summary>
    public int OwnEditChecks { get; set; }

    /// <summary>Own-edit checks where the edit was not visible (violation).</summary>
    public int OwnEditMissing { get; set; }

    /// <summary>Mutations whose refresh failed, so the own-edit check was skipped.</summary>
    public int OwnEditSkippedRefreshFailed { get; set; }

    /// <summary>Isolated-mode peer probes.</summary>
    public int CrossTalkProbes { get; set; }

    /// <summary>Isolated-mode probes where the peer saw this agent's edit (violation).</summary>
    public int CrossTalkViolations { get; set; }

    /// <summary>Shared-mode peer probes.</summary>
    public int SharedPeerProbes { get; set; }

    /// <summary>Shared-mode probes where the peer saw the edit (informational).</summary>
    public int SharedPeerSawEdit { get; set; }

    /// <summary>Records one of this agent's own calls.</summary>
    public void Record(McpCallResult result)
    {
        Calls++;
        if (!LatenciesMs.TryGetValue(result.Tool, out var list))
            LatenciesMs[result.Tool] = list = [];
        list.Add(result.Latency.TotalMilliseconds);
        if (!result.Ok)
        {
            ToolErrors[result.Tool] = ToolErrors.GetValueOrDefault(result.Tool) + 1;
            RecordError(result);
        }
    }

    /// <summary>Records a peer probe: errors only, no latency (it ran on the peer's client).</summary>
    public void RecordProbe(McpCallResult result)
    {
        if (!result.Ok) RecordError(result);
    }

    /// <summary>Adds a sample description if under the cap and not already present.</summary>
    public void AddSample(string description)
    {
        if (SampleErrors.Count < MaxSamples && !SampleErrors.Contains(description))
            SampleErrors.Add(description);
    }

    private void RecordError(McpCallResult result)
    {
        var code = result.ErrorCode ?? "UNKNOWN";
        ErrorsByCode[code] = ErrorsByCode.GetValueOrDefault(code) + 1;
        var message = result.ErrorMessage ?? "";
        if (message.Length > 300) message = message[..300] + "…";
        AddSample($"{result.Tool}: {code}: {message}");
    }
}
