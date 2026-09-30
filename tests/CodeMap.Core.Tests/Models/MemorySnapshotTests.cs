namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Models;
using FluentAssertions;

/// <summary>Tests for <see cref="MemorySnapshot"/> (PHASE-21-12 T01).</summary>
public class MemorySnapshotTests
{
    [Fact]
    public void Capture_ReturnsPositiveProcessAndHeapFigures()
    {
        GC.Collect(); // make sure GC.GetGCMemoryInfo describes at least one collection

        var snapshot = MemorySnapshot.Capture();

        snapshot.WorkingSetBytes.Should().BePositive();
        snapshot.PrivateBytes.Should().BePositive();
        snapshot.GcHeapBytes.Should().BePositive();
        snapshot.GcCommittedBytes.Should().BePositive();
        snapshot.GcFragmentedBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void LogArgs_EventThenMegabytes_MatchTemplatePlaceholders()
    {
        var snapshot = new MemorySnapshot(3L << 20, 2L << 20, 1L << 20, 0, 5L << 20);

        var args = snapshot.LogArgs("baseline_built");

        args.Should().Equal("baseline_built", 3L, 2L, 1L, 0L, 5L);
        MemorySnapshot.LogTemplate.Split('{').Length.Should().Be(args.Length + 1, "one placeholder per argument");
    }
}
