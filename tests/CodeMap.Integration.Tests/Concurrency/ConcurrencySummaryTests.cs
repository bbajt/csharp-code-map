namespace CodeMap.Integration.Tests.Concurrency;

using System.Text.Json;
using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>
/// Unit tests for <see cref="ConcurrencySummary"/> — the Markdown table the PHASE-21-06 results are
/// generated from (so numbers in docs are never hand-transcribed).
/// </summary>
public class ConcurrencySummaryTests
{
    private const long MiB = 1024L * 1024;
    private static readonly string Sha = "0123456789abcdef" + new string('0', 24);

    private const string ExpectedTable =
        "| Repo | Commit | OS | N | Mode | Calls/s | Calls/s/agent | search p50/p95 | get_card p50/p95 | refs.find p50/p95 | refresh p50/p95 | Peak WS total MB | ×N=1 | Peak WS/proc MB | Builds/req | Setup fail | Errors | Passed |\n" +
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n" +
        "| SampleSolution | 01234567 | Windows 11 | 1 | isolated | 50.0 | 50.0 | 2.0/4.0 | — | — | — | 500 | 1.00 | 500 | 1/1 | 0 | 0 | yes |\n" +
        "| SampleSolution | 01234567 | Windows 11 | 2 | isolated | 90.0 | 45.0 | 3.0/5.0 | — | — | 70.0/80.0 | 900 | 1.80 | 450 | 2/2 | 0 | TIMEOUT:1 | yes |\n" +
        "| SampleSolution | — | — | 4 | — | — | — | — | — | — | — | — | — | — | — | — | — | SKIPPED: projected 3.6 GB > budget 0.5 GB |\n";

    [Fact]
    public void ToMarkdown_RunsAndSkip_SortedByRepoThenN()
    {
        // Deliberately out of order: N=2 first, skipped entry in the same report.
        var report = new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN2(), RunN1()], [Skip()]);

        ConcurrencySummary.ToMarkdown([report]).Should().Be(ExpectedTable);
    }

    [Fact]
    public void ToMarkdown_IsDeterministic_AcrossCallsAndSplitReports()
    {
        var one = new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN1(), RunN2()], [Skip()]);
        var split = new[]
        {
            new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN2()], []),
            new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN1()], [Skip()]),
        };

        ConcurrencySummary.ToMarkdown([one]).Should().Be(ConcurrencySummary.ToMarkdown([one]));
        ConcurrencySummary.ToMarkdown(split).Should().Be(ConcurrencySummary.ToMarkdown([one]));
    }

    [Fact]
    public void Parse_RoundTripsAReportWrittenByTheHarness()
    {
        var report = new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN1(), RunN2()], [Skip()]);
        var json = JsonSerializer.Serialize(report, ConcurrencyJson.Options);

        var (parsed, error) = ConcurrencySummary.Parse(json);

        error.Should().BeNull();
        ConcurrencySummary.ToMarkdown([parsed!]).Should().Be(ExpectedTable);
    }

    [Fact]
    public void Parse_ReportWithoutSkippedField_IsAccepted()
    {
        // Reports written before PHASE-21-06 have no "skipped" property.
        var json = JsonSerializer.Serialize(
            new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN1()]), ConcurrencyJson.Options);
        var legacy = json.Replace("\"skipped\": null", "\"unused\": null");

        var (parsed, error) = ConcurrencySummary.Parse(legacy);

        error.Should().BeNull();
        parsed!.Runs.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_ForeignSchema_IsRefused()
    {
        var (parsed, error) = ConcurrencySummary.Parse("{\"schema\":\"codemap.other/9\",\"runs\":[]}");

        parsed.Should().BeNull();
        error.Should().Contain("codemap.other/9").And.Contain(ConcurrencyReport.SchemaV1);
    }

    [Fact]
    public void ToMarkdown_FailedRun_RowNamesTheError_OnOneLine_WithPipesEscaped()
    {
        var failed = new FailedRun("SampleSolution", 8, "InvalidOperationException: git clone failed (128): a|b\r\nfatal: too big");
        var report = new ConcurrencyReport(ConcurrencyReport.SchemaV1, [RunN1()], [], [failed]);

        var lines = ConcurrencySummary.ToMarkdown([report]).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(4);
        lines[3].Should().Be(
            "| SampleSolution | — | — | 8 | — | — | — | — | — | — | — | — | — | — | — | — | — | " +
            @"FAILED: InvalidOperationException: git clone failed (128): a\|b fatal: too big |");
    }

    private static SkippedRun Skip() => new("SampleSolution", 4, "projected 3.6 GB > budget 0.5 GB", 3.6, 0.5);

    private static ConcurrencyRunResult RunN1() => Run(
        agents: 1, callsPerSecond: 50.0, peakTotalMb: 500, perProcessMb: [500], builds: 1,
        errors: new Dictionary<string, int>(),
        tools: [ToolStats.From("symbols.search", [2.0, 4.0], 0)]);

    private static ConcurrencyRunResult RunN2() => Run(
        agents: 2, callsPerSecond: 90.0, peakTotalMb: 900, perProcessMb: [450, 450], builds: 2,
        errors: new Dictionary<string, int> { ["TIMEOUT"] = 1 },
        tools: [ToolStats.From("symbols.search", [3.0, 5.0], 0), ToolStats.From("index.refresh_overlay", [70.0, 80.0], 0)]);

    private static ConcurrencyRunResult Run(
        int agents, double callsPerSecond, long peakTotalMb, long[] perProcessMb, int builds,
        Dictionary<string, int> errors, IReadOnlyList<ToolStats> tools) => new(
        StartedUtc: new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero),
        Environment: new RunEnvironment("Windows 11", 32, "C:/x/CodeMap.Daemon.dll", "codemap-mcp 2.8.1", "SampleSolution", Sha),
        Agents: agents,
        WorkspaceMode: "isolated",
        Seed: 42,
        DurationSeconds: 60,
        ActualLoopSeconds: 60,
        TotalCalls: 100,
        LoopCalls: 90,
        CallsPerSecond: callsPerSecond,
        ErrorsByCode: errors,
        Tools: tools,
        Memory: new MemoryStats(peakTotalMb * MiB, 0, 0, 0, perProcessMb.Select(m => m * MiB).ToList(), 40),
        Setup: new SetupStats([], [], agents, builds, 0),
        Correctness: new CorrectnessStats(10, 10, 0, 0, 0, 0, 0, 0),
        SampleErrors: []);
}
