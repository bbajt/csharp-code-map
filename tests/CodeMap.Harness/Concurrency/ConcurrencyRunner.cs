namespace CodeMap.Harness.Concurrency;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CodeMap.Core.Errors;
using CodeMap.Daemon;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Runners;

/// <summary>
/// Drives topology T1: N real daemon processes sharing one <c>CODEMAP_HOME</c>, each serving
/// one agent. Phases per run: scratch repo → start clients → barrier → setup → timed loop →
/// idle → teardown → aggregate. See docs/PHASE-21-01.md §4.
/// </summary>
public sealed class ConcurrencyRunner
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private const string SharedWorkspaceId = "bench-shared";

    private readonly ConcurrencyOptions _options;
    private readonly IConcurrencyReporter _reporter;

    /// <summary>Creates a runner with the given options and reporter.</summary>
    public ConcurrencyRunner(ConcurrencyOptions options, IConcurrencyReporter reporter)
    {
        _options = options;
        _reporter = reporter;
    }

    /// <summary>
    /// Runs every (repo × agent count) combination and reports the aggregate. Agent counts run in
    /// ascending order per repo; a count whose projected peak memory exceeds the budget is not
    /// started and is recorded as a <see cref="SkippedRun"/>, together with every larger count
    /// for that repo (PHASE-21-06 T01). Skips don't affect the exit code. A run that throws is
    /// recorded as a <see cref="FailedRun"/> and the remaining runs continue; the exit code is then
    /// <see cref="HarnessExitCode.IndexBuildFailure"/> unless a correctness failure outranks it.
    /// </summary>
    public async Task<(ConcurrencyReport Report, int ExitCode)> RunAsync(
        IReadOnlyList<RepoDescriptor> repos, CancellationToken ct)
    {
        var budgetGb = _options.MaxMemoryGb
            ?? MemoryGuard.DefaultBudgetGb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var runs = new List<ConcurrencyRunResult>();
        var skipped = new List<SkippedRun>();
        var failed = new List<FailedRun>();
        foreach (var repo in repos)
        {
            var measured = new List<ConcurrencyRunResult>();
            SkippedRun? firstSkip = null;
            foreach (var n in _options.AgentCounts.Distinct().Order())
            {
                var projected = MemoryGuard.ProjectPeakGb(measured, n);
                if (firstSkip is not null || projected > budgetGb)
                {
                    var reason = firstSkip is null
                        ? string.Create(CultureInfo.InvariantCulture,
                            $"projected {projected:0.0##} GB > budget {budgetGb:0.0##} GB")
                        : $"N={firstSkip.Agents} already skipped ({firstSkip.Reason})";
                    var skip = new SkippedRun(repo.Name, n, reason, projected ?? 0, budgetGb);
                    firstSkip ??= skip;
                    _reporter.ReportRunSkipped(skip);
                    skipped.Add(skip);
                    continue;
                }

                _reporter.ReportRunStart(repo, n, _options);
                ConcurrencyRunResult result;
                try
                {
                    result = await RunOnceAsync(repo, n, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // Keep the invocation's other runs: the report is only written at the end.
                    var fail = new FailedRun(repo.Name, n, $"{ex.GetType().Name}: {ex.Message}");
                    _reporter.ReportRunFailed(fail);
                    failed.Add(fail);
                    continue;
                }
                _reporter.ReportRunComplete(result);
                runs.Add(result);
                measured.Add(result);
            }
        }

        var report = new ConcurrencyReport(ConcurrencyReport.SchemaV1, runs, skipped, failed);
        _reporter.ReportSummary(report);
        // A correctness violation outranks a run that couldn't execute.
        var exit = runs.Any(r => !r.Passed) ? HarnessExitCode.CorrectnessMismatch
            : failed.Count > 0 ? HarnessExitCode.IndexBuildFailure
            : HarnessExitCode.Success;
        return (report, (int)exit);
    }

    /// <summary>Executes one run of <paramref name="agents"/> agents against <paramref name="repo"/>.</summary>
    public async Task<ConcurrencyRunResult> RunOnceAsync(RepoDescriptor repo, int agents, CancellationToken ct)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var runRoot = Path.Combine(_options.DataRoot, "concurrency",
            $"{startedUtc:yyyyMMdd-HHmmss}-{repo.Name}-n{agents}-{_options.WorkspaceMode.ToString().ToLowerInvariant()}");
        var home = Path.Combine(runRoot, "home");

        var guard = ValidateHome(home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (guard.IsFailure)
            throw new InvalidOperationException(guard.Error.Message);

        var shared = _options.WorkspaceMode == WorkspaceMode.Shared;
        await using var scratch = await AgentScratchRepo.CreateAsync(
            repo.GitRepoRoot, Path.Combine(runRoot, "repo"), shared ? 1 : agents, ct).ConfigureAwait(false);
        scratch.Keep = _options.KeepArtifacts;

        var solutionRel = Path.GetRelativePath(repo.GitRepoRoot, repo.SolutionPath);
        var editRel = Path.GetRelativePath(repo.GitRepoRoot,
            FindEditTarget(Path.GetDirectoryName(repo.SolutionPath)!));

        // Before any daemon starts and outside every measured phase: an unrestored worktree
        // indexes with unresolved package references (F12), which would understate every figure.
        var restoreMs = _options.Restore
            ? Math.Round((await scratch.RestoreAsync(solutionRel, ct).ConfigureAwait(false)).TotalMilliseconds, 1)
            : 0;

        var env = new Dictionary<string, string?>
        {
            [CodeMapHome.EnvVar] = home,
            ["CODEMAP_CACHE_DIR"] = _options.CacheDir,
        };

        var clients = new List<IMcpClient>();
        using var exitHook = new ChildKillHook(clients);
        try
        {
            // Start all daemons before the barrier so process start-up isn't in the setup race.
            var starts = Enumerable.Range(0, agents).Select(i => StdioMcpClient.StartAsync(
                _options.DaemonPath, env, Path.Combine(runRoot, "logs", $"agent-{i}.stderr.log"),
                _options.CallTimeout, ct)).ToList();
            Exception? startFailure = null;
            foreach (var s in starts)
            {
                // Await every start so successfully started daemons are tracked (and disposed
                // in finally) even when a sibling fails.
                try { clients.Add(await s.ConfigureAwait(false)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { startFailure ??= ex; }
            }
            if (startFailure is not null)
                throw new InvalidOperationException($"Daemon start failed: {startFailure.Message}", startFailure);

            var editLock = new Lock();
            var workloads = clients.Select((c, i) =>
            {
                var wt = scratch.Worktrees[shared ? 0 : i];
                return new AgentWorkload(i, c, wt, Path.Combine(wt, solutionRel),
                    shared ? SharedWorkspaceId : $"agent-{i}", Path.Combine(wt, editRel),
                    _options.Seed, _options.WorkspaceMode, editLock, repo.KnownQueryInputs);
            }).ToList();
            foreach (var w in workloads) w.Peers = workloads;

            await using var sampler = new ProcessMemorySampler(clients.Select(c => c.ProcessId).ToList(), SampleInterval);

            // Setup — all agents released together (concurrent first index).
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var setups = workloads.Select(w => w.SetupAsync(gate.Task, ct)).ToList();
            gate.SetResult();
            var ready = await Task.WhenAll(setups).ConfigureAwait(false);

            // Timed loop.
            var loopStart = sampler.Now;
            var loopClock = Stopwatch.StartNew();
            await Task.WhenAll(workloads.Where((_, i) => ready[i])
                .Select(w => w.RunLoopAsync(_options.Duration, ct))).ConfigureAwait(false);
            loopClock.Stop();
            var loopEnd = sampler.Now;

            // Idle, then the post-idle sample.
            if (_options.Idle > TimeSpan.Zero)
                await Task.Delay(_options.Idle, ct).ConfigureAwait(false);
            var postIdle = sampler.SampleNow();
            var memory = sampler.Summarize(loopStart, loopEnd, postIdle);

            foreach (var w in workloads.Where((_, i) => ready[i]))
                await w.TeardownAsync(ct).ConfigureAwait(false);

            return Aggregate(repo, agents, startedUtc, scratch.CommitSha, loopClock.Elapsed, restoreMs,
                workloads, clients, memory);
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync().ConfigureAwait(false);
            if (!_options.KeepArtifacts)
                AgentScratchRepo.DeleteDirectoryRobust(home);
        }
    }

    /// <summary>
    /// Refuses a data root equal to, or inside, the developer's real default data root
    /// (<c>~/.codemap</c>) — benchmark runs must never touch the live store.
    /// </summary>
    public static Result<string, CodeMapError> ValidateHome(string home, string userProfile)
    {
        var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CodeMapHome.Resolve(null, userProfile).Value));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (candidate.Equals(real, comparison) ||
            candidate.StartsWith(real + Path.DirectorySeparatorChar, comparison))
            return Result<string, CodeMapError>.Failure(CodeMapError.InvalidArgument(
                $"Refusing to run the concurrency benchmark against the real data root '{real}' (resolved home '{candidate}')."));
        return candidate;
    }

    /// <summary>
    /// Picks the file mutations append to: the ordinal-first <c>.cs</c> file under the solution
    /// directory, excluding build output, generated files, entry points and test projects.
    /// </summary>
    public static string FindEditTarget(string solutionDir)
    {
        static bool Excluded(string rel)
        {
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var file = parts[^1];
            return parts.Any(p => p is "bin" or "obj" || p.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                || file is "Program.cs" or "AssemblyInfo.cs" or "GlobalUsings.cs"
                || file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(solutionDir, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(solutionDir, f)))
            .Where(f => !Excluded(f.Rel))
            .OrderBy(f => f.Rel.Replace('\\', '/'), StringComparer.Ordinal)
            .Select(f => f.Full)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No editable .cs file found under {solutionDir}");
    }

    private ConcurrencyRunResult Aggregate(
        RepoDescriptor repo, int agents, DateTimeOffset startedUtc, string commitSha, TimeSpan loopElapsed,
        double restoreMs, IReadOnlyList<AgentWorkload> workloads, IReadOnlyList<IMcpClient> clients, MemoryStats memory)
    {
        var stats = workloads.Select(w => w.Stats).ToList();

        var tools = stats.SelectMany(s => s.LatenciesMs.Keys).Distinct().Order(StringComparer.Ordinal)
            .Select(tool => ToolStats.From(tool,
                stats.SelectMany(s => s.LatenciesMs.GetValueOrDefault(tool) ?? []).ToList(),
                stats.Sum(s => s.ToolErrors.GetValueOrDefault(tool))))
            .ToList();

        var errorsByCode = stats.SelectMany(s => s.ErrorsByCode)
            .GroupBy(kv => kv.Key)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

        var totalCalls = stats.Sum(s => s.Calls);
        var loopCalls = stats.Sum(s => s.LoopCalls);

        return new ConcurrencyRunResult(
            StartedUtc: startedUtc,
            Environment: new RunEnvironment(
                Os: RuntimeInformation.OSDescription,
                ProcessorCount: Environment.ProcessorCount,
                DaemonPath: _options.DaemonPath,
                DaemonVersion: DaemonVersion(_options.DaemonPath),
                RepoName: repo.Name,
                CommitSha: commitSha),
            Agents: agents,
            WorkspaceMode: _options.WorkspaceMode.ToString().ToLowerInvariant(),
            Seed: _options.Seed,
            DurationSeconds: _options.Duration.TotalSeconds,
            ActualLoopSeconds: Math.Round(loopElapsed.TotalSeconds, 3),
            TotalCalls: totalCalls,
            LoopCalls: loopCalls,
            // Throughput = loop calls over loop time; setup calls (baseline builds) excluded.
            CallsPerSecond: loopCalls > 0 && loopElapsed.TotalSeconds >= 1
                ? Math.Round(loopCalls / loopElapsed.TotalSeconds, 2)
                : 0,
            ErrorsByCode: errorsByCode,
            Tools: tools,
            Memory: memory,
            Setup: new SetupStats(
                InitializeMs: clients.Select(c => Math.Round(c.InitializeElapsed.TotalMilliseconds, 1)).ToList(),
                TimeToFirstSuccessMs: stats.Select(s => Math.Round(s.FirstSuccessMs ?? -1, 1)).ToList(),
                BaselineRequests: stats.Sum(s => s.BaselineRequests),
                BaselineBuilds: stats.Sum(s => s.BaselineBuilds),
                AgentsFailedSetup: stats.Count(s => s.SetupFailed),
                RestoreMs: restoreMs),
            Correctness: new CorrectnessStats(
                Mutations: stats.Sum(s => s.Mutations),
                OwnEditChecks: stats.Sum(s => s.OwnEditChecks),
                OwnEditMissing: stats.Sum(s => s.OwnEditMissing),
                OwnEditSkippedRefreshFailed: stats.Sum(s => s.OwnEditSkippedRefreshFailed),
                CrossTalkProbes: stats.Sum(s => s.CrossTalkProbes),
                CrossTalkViolations: stats.Sum(s => s.CrossTalkViolations),
                SharedPeerProbes: stats.Sum(s => s.SharedPeerProbes),
                SharedPeerSawEdit: stats.Sum(s => s.SharedPeerSawEdit)),
            SampleErrors: stats.SelectMany(s => s.SampleErrors).Distinct().Take(40).ToList());
    }

    private static string DaemonVersion(string daemonPath)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(daemonPath);
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null) return "unknown";
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return output.Length == 0 ? "unknown" : output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Kills every started child if the harness process exits abruptly (Ctrl-C paths run the
    /// normal finally; this covers ProcessExit without it).
    /// </summary>
    private sealed class ChildKillHook : IDisposable
    {
        private readonly IReadOnlyList<IMcpClient> _clients;

        public ChildKillHook(IReadOnlyList<IMcpClient> clients)
        {
            _clients = clients;
            AppDomain.CurrentDomain.ProcessExit += OnExit;
        }

        public void Dispose() => AppDomain.CurrentDomain.ProcessExit -= OnExit;

        private void OnExit(object? sender, EventArgs e)
        {
            foreach (var c in _clients.ToArray())
            {
                try { Process.GetProcessById(c.ProcessId).Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                               or System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
