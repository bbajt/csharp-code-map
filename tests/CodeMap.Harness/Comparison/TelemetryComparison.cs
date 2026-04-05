namespace CodeMap.Harness.Comparison;

/// <summary>Per-query telemetry comparison between two engine executions.</summary>
public record TelemetryComparison(
    TimeSpan LeftElapsed,
    TimeSpan RightElapsed
)
{
    /// <summary>Latency ratio (right/left). Values below 1.0 mean right is faster.</summary>
    public double LatencyRatio =>
        LeftElapsed.TotalMilliseconds > 0
            ? RightElapsed.TotalMilliseconds / LeftElapsed.TotalMilliseconds
            : 1.0;

    public static TelemetryComparison SingleEngine(TimeSpan elapsed) =>
        new(elapsed, TimeSpan.Zero);
}
