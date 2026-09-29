namespace CodeMap.Harness.Concurrency;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Top-level concurrency report: one entry per (repo, agent-count) run. Serialized with
/// <see cref="ConcurrencyJson.Options"/>; <see cref="Schema"/> is versioned because later
/// phases commit these files and compare against them.
/// </summary>
/// <remarks>
/// <see cref="Skipped"/> and <see cref="Failed"/> (PHASE-21-06) are additive: reports written
/// earlier have no such fields and deserialize with them null. The schema identifier is unchanged.
/// </remarks>
public sealed record ConcurrencyReport(
    string Schema,
    IReadOnlyList<ConcurrencyRunResult> Runs,
    IReadOnlyList<SkippedRun>? Skipped = null,
    IReadOnlyList<FailedRun>? Failed = null)
{
    /// <summary>Current schema identifier.</summary>
    public const string SchemaV1 = "codemap.concurrency/1";

    /// <summary>
    /// True when every run passed its correctness checks and no run failed to execute.
    /// Skipped runs (memory budget) don't count against it.
    /// </summary>
    public bool Passed => Runs.All(r => r.Passed) && Failed is not { Count: > 0 };
}

/// <summary>
/// A (repo, agent-count) combination the runner did not start because its projected peak memory
/// exceeded the budget (<see cref="MemoryGuard"/>). Not a failure.
/// </summary>
public sealed record SkippedRun(string RepoName, int Agents, string Reason, double ProjectedPeakGb, double BudgetGb);

/// <summary>
/// A (repo, agent-count) run that threw instead of producing a result (e.g. the scratch clone or a
/// daemon start failed). Recorded so the other runs of the invocation are kept (PHASE-21-06).
/// </summary>
public sealed record FailedRun(string RepoName, int Agents, string Error);

/// <summary>Result of one run: N agents against one repo for a fixed duration.</summary>
public sealed record ConcurrencyRunResult(
    DateTimeOffset StartedUtc,
    RunEnvironment Environment,
    int Agents,
    string WorkspaceMode,
    int Seed,
    double DurationSeconds,
    double ActualLoopSeconds,
    int TotalCalls,
    int LoopCalls,
    double CallsPerSecond,
    IReadOnlyDictionary<string, int> ErrorsByCode,
    IReadOnlyList<ToolStats> Tools,
    MemoryStats Memory,
    SetupStats Setup,
    CorrectnessStats Correctness,
    IReadOnlyList<string> SampleErrors)
{
    /// <summary>
    /// Pass = every agent completed setup (otherwise correctness was not measured) and zero
    /// correctness violations. Tool errors and latencies are reported, not gated — gates are
    /// calibrated in PHASE-21-02.
    /// </summary>
    public bool Passed =>
        Setup.AgentsFailedSetup == 0
        && Correctness.OwnEditMissing == 0
        && Correctness.CrossTalkViolations == 0;
}

/// <summary>Where and against what the run executed.</summary>
public sealed record RunEnvironment(
    string Os,
    int ProcessorCount,
    string DaemonPath,
    string DaemonVersion,
    string RepoName,
    string CommitSha);

/// <summary>Latency distribution for one tool across all agents (probes excluded).</summary>
public sealed record ToolStats(
    string Tool,
    int Calls,
    int Errors,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    /// <summary>Builds stats from raw latencies (ms) using nearest-rank percentiles.</summary>
    public static ToolStats From(string tool, IReadOnlyCollection<double> latenciesMs, int errors)
    {
        var sorted = latenciesMs.Order().ToArray();
        return new ToolStats(tool, sorted.Length, errors,
            Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99),
            sorted.Length == 0 ? 0 : sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of an ascending-sorted array; 0 when empty.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>Memory totals across all daemon processes of a run.</summary>
public sealed record MemoryStats(
    long PeakTotalWorkingSetBytes,
    long SteadyTotalWorkingSetBytes,
    long PostIdleTotalWorkingSetBytes,
    long PeakTotalPrivateBytes,
    IReadOnlyList<long> PerProcessPeakWorkingSetBytes,
    int SampleCount);

/// <summary>
/// Setup-phase figures. <see cref="BaselineBuilds"/> counts <c>ensure_baseline</c> responses
/// with <c>already_existed: false</c> — N builds for N requests means no coalescing (today's T1).
/// </summary>
public sealed record SetupStats(
    IReadOnlyList<double> InitializeMs,
    IReadOnlyList<double> TimeToFirstSuccessMs,
    int BaselineRequests,
    int BaselineBuilds,
    int AgentsFailedSetup,
    double RestoreMs = 0);

/// <summary>
/// Correctness counters. Violations: an agent not seeing its own refreshed edit
/// (<see cref="OwnEditMissing"/>), or — isolated mode — a peer seeing it
/// (<see cref="CrossTalkViolations"/>). Shared mode records peer visibility without asserting.
/// </summary>
public sealed record CorrectnessStats(
    int Mutations,
    int OwnEditChecks,
    int OwnEditMissing,
    int OwnEditSkippedRefreshFailed,
    int CrossTalkProbes,
    int CrossTalkViolations,
    int SharedPeerProbes,
    int SharedPeerSawEdit);

/// <summary>Shared JSON options for concurrency reports (snake_case, indented).</summary>
public static class ConcurrencyJson
{
    /// <summary>Serializer options used for writing and reading reports.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
