namespace CodeMap.Integration.Tests.Concurrency;

using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>Unit tests for <see cref="MemoryGuard"/> (PHASE-21-06 T01).</summary>
public class MemoryGuardTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void DefaultBudgetGb_IsThreeQuartersOfPhysicalRam()
    {
        MemoryGuard.DefaultBudgetGb(64 * GiB).Should().Be(48.0);
    }

    [Fact]
    public void ProjectPeakGb_NothingMeasured_IsNull()
    {
        MemoryGuard.ProjectPeakGb([], agents: 4).Should().BeNull();
    }

    [Fact]
    public void ProjectPeakGb_UsesLargestPerProcessPeakAcrossRuns_TimesAgents()
    {
        // N=1 peaked at 1 GiB in its only process; N=4 had one process at 2 GiB. Projection for
        // N=8 is the worst single process seen (2 GiB) × 8, not an average.
        var n1 = RunWithPerProcessPeaks(1 * GiB);
        var n4 = RunWithPerProcessPeaks(GiB / 2, 2 * GiB, GiB, GiB);

        MemoryGuard.ProjectPeakGb([n1, n4], agents: 8).Should().Be(16.0);
    }

    [Fact]
    public void ProjectPeakGb_RunWithNoMemorySamples_IsIgnored()
    {
        var empty = RunWithPerProcessPeaks();

        MemoryGuard.ProjectPeakGb([empty], agents: 4).Should().BeNull();
    }

    private static ConcurrencyRunResult RunWithPerProcessPeaks(params long[] peaks) => new(
        StartedUtc: DateTimeOffset.UnixEpoch,
        Environment: new RunEnvironment("os", 1, "d", "v", "Repo", new string('a', 40)),
        Agents: Math.Max(1, peaks.Length),
        WorkspaceMode: "isolated",
        Seed: 42,
        DurationSeconds: 1,
        ActualLoopSeconds: 1,
        TotalCalls: 0,
        LoopCalls: 0,
        CallsPerSecond: 0,
        ErrorsByCode: new Dictionary<string, int>(),
        Tools: [],
        Memory: new MemoryStats(peaks.Sum(), 0, 0, 0, peaks, peaks.Length == 0 ? 0 : 10),
        Setup: new SetupStats([], [], 0, 0, 0),
        Correctness: new CorrectnessStats(0, 0, 0, 0, 0, 0, 0, 0),
        SampleErrors: []);
}
