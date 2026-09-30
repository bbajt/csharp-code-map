namespace CodeMap.Daemon.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Tests for <see cref="ActivityMonitor"/> (PHASE-21-12 T02).</summary>
public class ActivityMonitorTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void BeginRequest_InFlightUntilDisposed_LastActivityAtStartAndEnd()
    {
        var clock = new ManualClock();
        var monitor = new ActivityMonitor(NullLogger<ActivityMonitor>.Instance, clock);

        var scope = monitor.BeginRequest();
        monitor.RequestInFlight.Should().BeTrue();

        clock.Now += TimeSpan.FromSeconds(30);
        scope.Dispose();
        scope.Dispose(); // idempotent

        monitor.RequestInFlight.Should().BeFalse();
        monitor.LastActivityUtc.Should().Be(clock.Now, "the end of a request is activity too");
    }

    [Fact]
    public void MarkHeavyWork_RecordsTime_NullBefore()
    {
        var clock = new ManualClock();
        var monitor = new ActivityMonitor(NullLogger<ActivityMonitor>.Instance, clock);
        monitor.LastHeavyWorkUtc.Should().BeNull();

        clock.Now += TimeSpan.FromMinutes(1);
        monitor.MarkHeavyWork("baseline_built");

        monitor.LastHeavyWorkUtc.Should().Be(clock.Now);
        monitor.LastActivityUtc.Should().NotBe(clock.Now, "heavy work is not a request");
    }
}
