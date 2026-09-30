namespace CodeMap.Daemon.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Policy tests for <see cref="IdleMemoryReclaimer"/> (PHASE-21-12 T02, ADR-061): a fake clock, the real
/// <see cref="ActivityMonitor"/>, and a recording seam instead of the GC and the incremental compiler.
/// </summary>
public class IdleMemoryReclaimerTests
{
    private const long Mb = 1024 * 1024;

    private readonly ManualClock _clock = new();
    private readonly RecordingActions _actions = new();
    private readonly ActivityMonitor _activity;
    private readonly IdleMemoryReclaimer _reclaimer;

    public IdleMemoryReclaimerTests()
    {
        _activity = new ActivityMonitor(NullLogger<ActivityMonitor>.Instance, _clock);
        _reclaimer = new IdleMemoryReclaimer(_activity, _actions, NullLogger<IdleMemoryReclaimer>.Instance, _clock);
    }

    [Fact]
    public void NoHeavyWork_NeverCollects()
    {
        _clock.Advance(TimeSpan.FromHours(1));

        _reclaimer.Tick();

        _actions.Collections.Should().Be(0);
    }

    [Fact]
    public void HeavyWork_QuietPeriodNotReached_DoesNotCollect()
    {
        Request();
        _activity.MarkHeavyWork("baseline_built");
        _clock.Advance(TimeSpan.FromSeconds(119));

        _reclaimer.Tick();

        _actions.Collections.Should().Be(0);
    }

    [Fact]
    public void HeavyWork_Quiet_HeapAboveThreshold_CollectsOnce()
    {
        Request();
        _activity.MarkHeavyWork("baseline_built");
        _clock.Advance(TimeSpan.FromSeconds(121));

        _reclaimer.Tick();
        _clock.Advance(TimeSpan.FromMinutes(5));
        _reclaimer.Tick();

        _actions.Collections.Should().Be(1, "at most once per heavy-work episode");
        _reclaimer.LastReclaimUtc.Should().NotBeNull();
    }

    [Fact]
    public void NewHeavyWorkAfterReclaim_CollectsAgain()
    {
        _activity.MarkHeavyWork("baseline_built");
        _clock.Advance(TimeSpan.FromMinutes(3));
        _reclaimer.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        Request();
        _activity.MarkHeavyWork("solution_opened");
        _clock.Advance(TimeSpan.FromMinutes(3));
        _reclaimer.Tick();

        _actions.Collections.Should().Be(2);
    }

    [Fact]
    public void RequestInFlight_DoesNotCollect()
    {
        _activity.MarkHeavyWork("baseline_built");
        using var request = _activity.BeginRequest();
        _clock.Advance(TimeSpan.FromMinutes(5));

        _reclaimer.Tick();

        _actions.Collections.Should().Be(0);
        _actions.EvictionChecks.Should().Be(0, "nothing runs while a request is in flight");
    }

    [Fact]
    public void HeapBelowThreshold_DoesNotCollect()
    {
        _actions.HeapBytes = 100 * Mb;
        _activity.MarkHeavyWork("baseline_built");
        _clock.Advance(TimeSpan.FromMinutes(5));

        _reclaimer.Tick();

        _actions.Collections.Should().Be(0);
    }

    [Fact]
    public void SolutionIdle10Min_Evicts_ThenCollects()
    {
        _actions.LastRefreshUtc = _clock.GetUtcNow();   // a refresh opened the solution; its GC already ran
        _clock.Advance(TimeSpan.FromMinutes(9));
        _reclaimer.Tick();
        _actions.Evictions.Should().Be(0, "9 minutes is not idle enough");

        _clock.Advance(TimeSpan.FromMinutes(2));
        _reclaimer.Tick();

        _actions.Evictions.Should().Be(1);
        _actions.Collections.Should().Be(1, "an eviction is heavy work: the freed solution is collected");
    }

    private void Request()
    {
        using var request = _activity.BeginRequest();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingActions : IdleMemoryReclaimer.IReclaimActions
    {
        public long HeapBytes { get; set; } = 900 * Mb;

        public DateTimeOffset? LastRefreshUtc { get; set; }

        public int EvictionChecks { get; private set; }

        public int Evictions { get; private set; }

        public int Collections { get; private set; }

        public bool EvictIdleSolution(TimeSpan idleFor, DateTimeOffset now)
        {
            EvictionChecks++;
            if (LastRefreshUtc is not { } last || now - last < idleFor) return false;
            LastRefreshUtc = null;
            Evictions++;
            return true;
        }

        public long GcHeapBytes() => HeapBytes;

        public void CompactingCollect() => Collections++;
    }
}
