namespace CodeMap.Harness.Concurrency;

using System.Globalization;

/// <summary>How agents map to workspaces.</summary>
public enum WorkspaceMode
{
    /// <summary>Each agent has its own worktree and workspace id (<c>agent-{i}</c>).</summary>
    Isolated,

    /// <summary>All agents share one worktree and one workspace id (<c>bench-shared</c>).</summary>
    Shared,
}

/// <summary>Options for the <c>concurrency</c> harness mode.</summary>
public sealed record ConcurrencyOptions(
    string DataRoot,
    string DaemonPath,
    IReadOnlyList<int> AgentCounts,
    TimeSpan Duration,
    TimeSpan Idle,
    int Seed,
    WorkspaceMode WorkspaceMode,
    string? CacheDir,
    bool KeepArtifacts,
    TimeSpan CallTimeout,
    IReadOnlyList<string>? RepoNames = null,
    double? MaxMemoryGb = null,
    bool Restore = true)
{
    /// <summary>
    /// Parses CLI arguments (everything after the mode name). Returns an error message
    /// instead of options on invalid input.
    /// </summary>
    public static (ConcurrencyOptions? Options, string? Error) Parse(IReadOnlyList<string> args, string dataRoot)
    {
        var agentsArg = Get(args, "--agents") ?? "1,4";
        var agentCounts = new List<int>();
        foreach (var part in agentsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 64)
                return (null, $"--agents: '{part}' is not an integer in 1..64");
            agentCounts.Add(n);
        }
        if (agentCounts.Count == 0) return (null, "--agents: at least one count required");

        if (!TryPositiveInt(args, "--duration", 60, out var duration, out var err)) return (null, err);
        if (!TryPositiveInt(args, "--idle", 10, out var idle, out err, allowZero: true)) return (null, err);
        if (!TryPositiveInt(args, "--seed", 42, out var seed, out err, allowZero: true)) return (null, err);
        if (!TryPositiveInt(args, "--call-timeout", 120, out var callTimeout, out err)) return (null, err);

        var modeArg = (Get(args, "--workspace-mode") ?? "isolated").ToLowerInvariant();
        WorkspaceMode mode;
        switch (modeArg)
        {
            case "isolated": mode = WorkspaceMode.Isolated; break;
            case "shared": mode = WorkspaceMode.Shared; break;
            default: return (null, $"--workspace-mode: expected isolated|shared, got '{modeArg}'");
        }

        IReadOnlyList<string>? repoNames = null;
        var repoNameArg = Get(args, "--repo-name");
        if (repoNameArg is not null)
        {
            if (Get(args, "--repo") is not null)
                return (null, "--repo-name and --repo are mutually exclusive: pass one repo list or one tier");
            repoNames = repoNameArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (repoNames.Count == 0) return (null, "--repo-name: at least one repo name required");
        }

        double? maxMemoryGb = null;
        var maxMemoryArg = Get(args, "--max-memory-gb");
        if (maxMemoryArg is not null)
        {
            if (!double.TryParse(maxMemoryArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb)
                || !(gb > 0) || double.IsInfinity(gb))
                return (null, $"--max-memory-gb: '{maxMemoryArg}' is not a positive number");
            maxMemoryGb = gb;
        }

        var daemon = Get(args, "--daemon") ?? DefaultDaemonPath();
        if (!File.Exists(daemon))
            return (null, $"--daemon: '{daemon}' not found (build src/CodeMap.Daemon or pass --daemon)");

        return (new ConcurrencyOptions(
            DataRoot: dataRoot,
            DaemonPath: Path.GetFullPath(daemon),
            AgentCounts: agentCounts,
            Duration: TimeSpan.FromSeconds(duration),
            Idle: TimeSpan.FromSeconds(idle),
            Seed: seed,
            WorkspaceMode: mode,
            CacheDir: Get(args, "--cache-dir"),
            KeepArtifacts: args.Contains("--keep"),
            CallTimeout: TimeSpan.FromSeconds(callTimeout),
            RepoNames: repoNames,
            MaxMemoryGb: maxMemoryGb,
            Restore: !args.Contains("--no-restore")), null);
    }

    /// <summary>
    /// The build-under-test daemon copied next to this assembly by the harness's project
    /// reference. Never falls back to the installed <c>~/.codemap/bin</c>.
    /// </summary>
    public static string DefaultDaemonPath() => Path.Combine(AppContext.BaseDirectory, "CodeMap.Daemon.dll");

    private static string? Get(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static bool TryPositiveInt(
        IReadOnlyList<string> args, string name, int fallback, out int value, out string? error, bool allowZero = false)
    {
        error = null;
        var raw = Get(args, name);
        if (raw is null) { value = fallback; return true; }
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) && (value > 0 || (allowZero && value == 0)))
            return true;
        error = $"{name}: '{raw}' is not a {(allowZero ? "non-negative" : "positive")} integer";
        return false;
    }
}
