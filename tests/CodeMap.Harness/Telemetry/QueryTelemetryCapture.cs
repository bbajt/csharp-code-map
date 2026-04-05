namespace CodeMap.Harness.Telemetry;

/// <summary>
/// Captures per-query telemetry deltas (counter snapshots before/after execution).
/// Phase 1: stub returning zero counters. Full instrumentation wired in Phase 5
/// when StorageTelemetry ActivitySource and Meter are implemented.
/// </summary>
public sealed class QueryTelemetryCapture : IDisposable
{
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }
    public long TombstoneApplications { get; private set; }
    public long MergeOverlayHits { get; private set; }
    public TimeSpan ActivityDuration { get; private set; }

    public void Begin()
    {
        // Phase 1: no-op — instrumentation added in Phase 5
    }

    public void End()
    {
        // Phase 1: no-op
    }

    public void Dispose() { }
}
