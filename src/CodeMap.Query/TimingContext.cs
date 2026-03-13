namespace CodeMap.Query;

using System.Diagnostics;
using CodeMap.Core.Models;

/// <summary>
/// Lightweight per-request timing helper. Accumulates wall-time measurements
/// for each query phase and produces a <see cref="TimingBreakdown"/> at the end.
/// </summary>
public sealed class TimingContext
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _phase = new();
    private double _cacheLookupMs;
    private double _dbQueryMs;
    private double _roslynCompileMs;
    private double _rankingMs;

    /// <summary>Starts the phase stopwatch (must be followed by an End* call).</summary>
    public void StartPhase() => _phase.Restart();

    /// <summary>Records the elapsed phase time as a cache-lookup measurement.</summary>
    public void EndCacheLookup()
    {
        _cacheLookupMs += _phase.Elapsed.TotalMilliseconds;
        _phase.Reset();
    }

    /// <summary>Records the elapsed phase time as a DB-query measurement.</summary>
    public void EndDbQuery()
    {
        _dbQueryMs += _phase.Elapsed.TotalMilliseconds;
        _phase.Reset();
    }

    /// <summary>Records the elapsed phase time as a Roslyn-compile measurement.</summary>
    public void EndRoslynCompile()
    {
        _roslynCompileMs += _phase.Elapsed.TotalMilliseconds;
        _phase.Reset();
    }

    /// <summary>Records the elapsed phase time as a ranking/post-processing measurement.</summary>
    public void EndRanking()
    {
        _rankingMs += _phase.Elapsed.TotalMilliseconds;
        _phase.Reset();
    }

    /// <summary>
    /// Stops the total stopwatch and returns the accumulated <see cref="TimingBreakdown"/>.
    /// This method should be called exactly once, after all phases are complete.
    /// </summary>
    public TimingBreakdown Build()
    {
        _total.Stop();
        return new TimingBreakdown(
            TotalMs: _total.Elapsed.TotalMilliseconds,
            CacheLookupMs: _cacheLookupMs,
            DbQueryMs: _dbQueryMs,
            RoslynCompileMs: _roslynCompileMs,
            RankingMs: _rankingMs);
    }
}
