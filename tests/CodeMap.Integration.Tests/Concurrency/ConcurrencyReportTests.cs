namespace CodeMap.Integration.Tests.Concurrency;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>
/// Unit tests for the concurrency result schema (<c>codemap.concurrency/1</c>), percentile
/// maths and pass rules. No processes are started.
/// </summary>
public class ConcurrencyReportTests
{
    [Fact]
    public void Json_RoundtripsSchemaV1()
    {
        var report = new ConcurrencyReport(ConcurrencyReport.SchemaV1, [Sample()]);

        var json = JsonSerializer.Serialize(report, ConcurrencyJson.Options);
        var back = JsonSerializer.Deserialize<ConcurrencyReport>(json, ConcurrencyJson.Options)!;

        back.Schema.Should().Be("codemap.concurrency/1");
        back.Runs.Should().ContainSingle();
        back.Runs[0].Should().BeEquivalentTo(report.Runs[0]);
    }

    [Fact]
    public void Json_UsesSnakeCaseAndIncludesPassed()
    {
        var json = JsonSerializer.Serialize(new ConcurrencyReport(ConcurrencyReport.SchemaV1, [Sample()]), ConcurrencyJson.Options);
        var run = JsonNode.Parse(json)!["runs"]![0]!;

        run["calls_per_second"].Should().NotBeNull();
        run["memory"]!["peak_total_working_set_bytes"].Should().NotBeNull();
        run["correctness"]!["cross_talk_violations"].Should().NotBeNull();
        run["passed"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(99, 10)]
    [InlineData(10, 1)]
    public void Percentile_NearestRank_OnOneToTen(double p, double expected)
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        ToolStats.Percentile(sorted, p).Should().Be(expected);
    }

    [Fact]
    public void Percentile_Empty_IsZero() => ToolStats.Percentile([], 95).Should().Be(0);

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void Passed_FalseOnSetupFailureOrViolation(int failedSetup, int ownMissing, int crossTalk)
    {
        var run = Sample() with
        {
            Setup = Sample().Setup with { AgentsFailedSetup = failedSetup },
            Correctness = Sample().Correctness with { OwnEditMissing = ownMissing, CrossTalkViolations = crossTalk },
        };

        run.Passed.Should().BeFalse();
        new ConcurrencyReport(ConcurrencyReport.SchemaV1, [Sample(), run]).Passed.Should().BeFalse();
    }

    [Fact]
    public void Passed_FalseWhenAnyRunFailedToExecute()
    {
        var report = new ConcurrencyReport(ConcurrencyReport.SchemaV1, [Sample()], [],
            [new FailedRun("BitwardenServer", 8, "InvalidOperationException: Daemon start failed")]);

        report.Passed.Should().BeFalse("a run that never produced numbers is not a pass");
    }

    [Fact]
    public void Json_FailedRuns_RoundTrip()
    {
        var failed = new FailedRun("eShopOnWeb", 4, "InvalidOperationException: git clone failed (128)");
        var json = JsonSerializer.Serialize(
            new ConcurrencyReport(ConcurrencyReport.SchemaV1, [Sample()], [], [failed]), ConcurrencyJson.Options);

        var back = JsonSerializer.Deserialize<ConcurrencyReport>(json, ConcurrencyJson.Options)!;

        back.Failed.Should().ContainSingle().Which.Should().Be(failed);
        JsonNode.Parse(json)!["failed"]![0]!["repo_name"]!.GetValue<string>().Should().Be("eShopOnWeb");
    }

    private static ConcurrencyRunResult Sample() => new(
        StartedUtc: new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero),
        Environment: new RunEnvironment("Windows 11", 16, "C:/x/CodeMap.Daemon.dll", "codemap-mcp 2.8.1", "SampleSolution", new string('a', 40)),
        Agents: 2,
        WorkspaceMode: "isolated",
        Seed: 42,
        DurationSeconds: 10,
        ActualLoopSeconds: 10.01,
        TotalCalls: 120,
        LoopCalls: 100,
        CallsPerSecond: 9.99,
        ErrorsByCode: new Dictionary<string, int> { ["TIMEOUT"] = 1 },
        Tools: [ToolStats.From("symbols.search", [1.0, 2.0, 3.0], 0)],
        Memory: new MemoryStats(900, 800, 850, 1000, [450, 450], 40),
        Setup: new SetupStats([100.0, 110.0], [3000.0, 3100.0], 2, 2, 0),
        Correctness: new CorrectnessStats(10, 10, 0, 0, 10, 0, 0, 0),
        SampleErrors: ["graph.callers: TIMEOUT: no response within 120s"]);
}
