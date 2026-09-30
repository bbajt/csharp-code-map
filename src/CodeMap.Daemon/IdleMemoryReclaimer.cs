namespace CodeMap.Daemon;

using System.Diagnostics;
using System.Runtime;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Roslyn;
using Microsoft.Extensions.Logging;

/// <summary>
/// Returns idle memory to the OS (PHASE-21-12 T02, ADR-061). Every <see cref="Defaults.CheckInterval"/>, while no
/// request is in flight:
/// <list type="number">
/// <item>evicts the incremental compiler's cached <c>Solution</c> after <see cref="Defaults.SolutionIdle"/> without
/// an overlay refresh, which counts as heavy work;</item>
/// <item>after heavy work (a baseline build, a cold solution open, an eviction), once no request has come for
/// <see cref="Defaults.QuietPeriod"/> and the GC heap is at least <see cref="Defaults.HeapThresholdBytes"/>,
/// runs one compacting, memory-returning full GC, logged before and after. At most once per heavy-work
/// episode.</item>
/// </list>
/// Started by <c>Program</c> after the host is built; disposed on exit.
/// </summary>
public sealed class IdleMemoryReclaimer : IAsyncDisposable
{
    /// <summary>Policy constants (ADR-061; no knobs, by decision D3).</summary>
    public static class Defaults
    {
        /// <summary>How often the reclaimer checks.</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

        /// <summary>No request for this long before a compacting GC.</summary>
        public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(120);

        /// <summary>No overlay refresh for this long before the cached solution is evicted.</summary>
        public static readonly TimeSpan SolutionIdle = TimeSpan.FromMinutes(10);

        /// <summary>Below this GC heap size a compacting GC isn't worth a pause.</summary>
        public const long HeapThresholdBytes = 256L * 1024 * 1024;
    }

    private readonly IActivityMonitor _activity;
    private readonly IReclaimActions _actions;
    private readonly TimeProvider _time;
    private readonly ILogger<IdleMemoryReclaimer> _logger;
    private ITimer? _timer;
    private int _ticking;

    /// <summary>Creates the reclaimer over the process's activity monitor and incremental compiler.</summary>
    public IdleMemoryReclaimer(IActivityMonitor activity, IncrementalCompiler compiler,
        ILogger<IdleMemoryReclaimer> logger, TimeProvider? time = null)
        : this(activity, new ProcessReclaimActions(compiler), logger, time)
    {
    }

    /// <summary>Test seam: <paramref name="actions"/> replaces eviction, heap size and the GC call.</summary>
    internal IdleMemoryReclaimer(IActivityMonitor activity, IReclaimActions actions,
        ILogger<IdleMemoryReclaimer> logger, TimeProvider? time = null)
    {
        _activity = activity;
        _actions = actions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>When the last compacting GC started (its tick time), or <c>null</c>.</summary>
    internal DateTimeOffset? LastReclaimUtc { get; private set; }

    /// <summary>Starts the periodic check.</summary>
    public void Start() =>
        _timer ??= _time.CreateTimer(_ => Tick(), null, Defaults.CheckInterval, Defaults.CheckInterval);

    /// <summary>One check (the timer callback; tests call it directly).</summary>
    internal void Tick()
    {
        // Timer callbacks can overlap if one takes longer than the interval (a long GC); run one at a time.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (_activity.RequestInFlight) return;
            var now = _time.GetUtcNow();

            if (_actions.EvictIdleSolution(Defaults.SolutionIdle, now))
            {
                _logger.LogInformation(MemorySnapshot.LogTemplate, MemorySnapshot.Capture().LogArgs("solution_evicted"));
                _activity.MarkHeavyWork("solution_evicted");
            }

            var heavy = _activity.LastHeavyWorkUtc;
            if (heavy is null || (LastReclaimUtc is { } last && last >= heavy)) return;   // nothing new to reclaim
            if (now - _activity.LastActivityUtc < Defaults.QuietPeriod) return;
            if (_actions.GcHeapBytes() < Defaults.HeapThresholdBytes) return;

            _logger.LogInformation(MemorySnapshot.LogTemplate, MemorySnapshot.Capture().LogArgs("reclaim_before"));
            var sw = Stopwatch.StartNew();
            _actions.CompactingCollect();
            sw.Stop();
            LastReclaimUtc = now;   // the start: heavy work that finishes during the GC gets its own reclaim
            _logger.LogInformation(MemorySnapshot.LogTemplate, MemorySnapshot.Capture().LogArgs("reclaim_after"));
            _logger.LogInformation("Idle memory reclaim: compacting full GC took {Ms} ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Never let a timer callback take the process down; the next tick tries again.
            _logger.LogWarning(ex, "Idle memory reclaim failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The effects the reclaimer has on the process (a seam for tests).</summary>
    internal interface IReclaimActions
    {
        /// <summary>Evicts the cached solution if it is idle for <paramref name="idleFor"/>; true if evicted.</summary>
        bool EvictIdleSolution(TimeSpan idleFor, DateTimeOffset now);

        /// <summary>The GC heap size as of the last GC.</summary>
        long GcHeapBytes();

        /// <summary>A blocking, compacting, memory-returning full GC (LOH included).</summary>
        void CompactingCollect();
    }

    /// <summary>The real effects: <see cref="IncrementalCompiler.EvictIfIdle"/> and the GC.</summary>
    private sealed class ProcessReclaimActions(IncrementalCompiler compiler) : IReclaimActions
    {
        /// <inheritdoc/>
        public bool EvictIdleSolution(TimeSpan idleFor, DateTimeOffset now) => compiler.EvictIfIdle(idleFor, now);

        /// <inheritdoc/>
        public long GcHeapBytes() => GC.GetGCMemoryInfo().HeapSizeBytes;

        /// <inheritdoc/>
        public void CompactingCollect()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }
}
