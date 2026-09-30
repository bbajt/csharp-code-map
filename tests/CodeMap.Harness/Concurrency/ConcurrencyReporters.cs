namespace CodeMap.Harness.Concurrency;

using System.Globalization;
using System.Text.Json;
using CodeMap.Harness.Repos;

/// <summary>
/// Output sink for concurrency runs. Separate from <see cref="Reports.IHarnessReporter"/>,
/// which is shaped for query-parity results.
/// </summary>
public interface IConcurrencyReporter
{
    /// <summary>Called before a run starts.</summary>
    void ReportRunStart(RepoDescriptor repo, int agents, ConcurrencyOptions options);

    /// <summary>Called after a run completes.</summary>
    void ReportRunComplete(ConcurrencyRunResult result);

    /// <summary>Called instead of a run that the memory guard did not start.</summary>
    void ReportRunSkipped(SkippedRun skipped);

    /// <summary>Called instead of <see cref="ReportRunComplete"/> when a run threw.</summary>
    void ReportRunFailed(FailedRun failed);

    /// <summary>Called once with the full report.</summary>
    void ReportSummary(ConcurrencyReport report);
}

/// <summary>Human-readable console output.</summary>
public sealed class ConcurrencyConsoleReporter : IConcurrencyReporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <inheritdoc/>
    public void ReportRunStart(RepoDescriptor repo, int agents, ConcurrencyOptions options) =>
        Console.WriteLine(
            $"▶ {repo.Name}: {agents} agent(s), {options.WorkspaceMode.ToString().ToLowerInvariant()}, " +
            $"{options.Duration.TotalSeconds:0}s, seed {options.Seed}");

    /// <inheritdoc/>
    public void ReportRunComplete(ConcurrencyRunResult r)
    {
        Console.WriteLine(string.Create(Inv,
            $"  {(r.Passed ? "PASS" : "FAIL")}  calls {r.TotalCalls} ({r.CallsPerSecond:0.0}/s) · " +
            $"errors {r.ErrorsByCode.Values.Sum()} · baseline builds {r.Setup.BaselineBuilds}/{r.Agents} · " +
            $"setup failures {r.Setup.AgentsFailedSetup}"));
        Console.WriteLine(string.Create(Inv,
            $"  memory  peak {Mb(r.Memory.PeakTotalWorkingSetBytes)} · steady {Mb(r.Memory.SteadyTotalWorkingSetBytes)} · " +
            $"post-idle {Mb(r.Memory.PostIdleTotalWorkingSetBytes)} (total WS across {r.Agents} process(es)) · " +
            $"private peak {Mb(r.Memory.PeakTotalPrivateBytes)} · post-idle {Mb(r.Memory.PostIdleTotalPrivateBytes)}"));
        Console.WriteLine(string.Create(Inv,
            $"  correctness  own-edit missing {r.Correctness.OwnEditMissing}/{r.Correctness.OwnEditChecks} · " +
            $"cross-talk {r.Correctness.CrossTalkViolations}/{r.Correctness.CrossTalkProbes} · " +
            $"refresh-failed skips {r.Correctness.OwnEditSkippedRefreshFailed}"));
        Console.WriteLine($"  {"tool",-24} {"calls",6} {"err",5} {"p50",8} {"p95",8} {"p99",8} {"max",8}");
        foreach (var t in r.Tools)
            Console.WriteLine(string.Create(Inv,
                $"  {t.Tool,-24} {t.Calls,6} {t.Errors,5} {t.P50Ms,8:0.0} {t.P95Ms,8:0.0} {t.P99Ms,8:0.0} {t.MaxMs,8:0.0}"));
        foreach (var (code, count) in r.ErrorsByCode)
            Console.WriteLine($"  error {code}: {count}");
        foreach (var sample in r.SampleErrors.Take(10))
            Console.WriteLine($"    · {sample}");
    }

    /// <inheritdoc/>
    public void ReportRunSkipped(SkippedRun skipped) =>
        Console.WriteLine($"SKIPPED {skipped.RepoName} N={skipped.Agents}: {skipped.Reason}");

    /// <inheritdoc/>
    public void ReportRunFailed(FailedRun failed) =>
        Console.WriteLine($"  FAILED  {failed.RepoName} N={failed.Agents}: {failed.Error}");

    /// <inheritdoc/>
    public void ReportSummary(ConcurrencyReport report) =>
        Console.WriteLine($"{(report.Passed ? "✓" : "✗")} {report.Runs.Count} run(s), " +
                          $"{report.Runs.Count(r => r.Passed)} passed correctness" +
                          (report.Skipped is { Count: > 0 } sk ? $", {sk.Count} skipped (memory budget)" : "") +
                          (report.Failed is { Count: > 0 } f ? $", {f.Count} FAILED to run." : "."));

    private static string Mb(long bytes) => string.Create(Inv, $"{bytes / (1024.0 * 1024.0):0} MB");
}

/// <summary>Writes the full report as JSON (schema <c>codemap.concurrency/1</c>) to a file or stdout.</summary>
public sealed class ConcurrencyJsonReporter : IConcurrencyReporter
{
    private readonly string? _outFile;

    /// <summary>Creates the reporter; null <paramref name="outFile"/> writes to stdout.</summary>
    public ConcurrencyJsonReporter(string? outFile) => _outFile = outFile;

    /// <inheritdoc/>
    public void ReportRunStart(RepoDescriptor repo, int agents, ConcurrencyOptions options) { }

    /// <inheritdoc/>
    public void ReportRunComplete(ConcurrencyRunResult result) { }

    /// <inheritdoc/>
    public void ReportRunSkipped(SkippedRun skipped) { }

    /// <inheritdoc/>
    public void ReportRunFailed(FailedRun failed) { }

    /// <inheritdoc/>
    public void ReportSummary(ConcurrencyReport report)
    {
        var json = JsonSerializer.Serialize(report, ConcurrencyJson.Options);
        if (_outFile is null)
        {
            Console.WriteLine(json);
            return;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(_outFile));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(_outFile, json);
    }
}

/// <summary>Fans out to several reporters (console progress + JSON file).</summary>
public sealed class CompositeConcurrencyReporter(params IConcurrencyReporter[] reporters) : IConcurrencyReporter
{
    /// <inheritdoc/>
    public void ReportRunStart(RepoDescriptor repo, int agents, ConcurrencyOptions options)
    {
        foreach (var r in reporters) r.ReportRunStart(repo, agents, options);
    }

    /// <inheritdoc/>
    public void ReportRunComplete(ConcurrencyRunResult result)
    {
        foreach (var r in reporters) r.ReportRunComplete(result);
    }

    /// <inheritdoc/>
    public void ReportRunSkipped(SkippedRun skipped)
    {
        foreach (var r in reporters) r.ReportRunSkipped(skipped);
    }

    /// <inheritdoc/>
    public void ReportRunFailed(FailedRun failed)
    {
        foreach (var r in reporters) r.ReportRunFailed(failed);
    }

    /// <inheritdoc/>
    public void ReportSummary(ConcurrencyReport report)
    {
        foreach (var r in reporters) r.ReportSummary(report);
    }
}
