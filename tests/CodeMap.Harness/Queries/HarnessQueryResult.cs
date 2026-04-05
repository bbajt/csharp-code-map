namespace CodeMap.Harness.Queries;

using CodeMap.Harness.Comparison;
using CodeMap.Harness.Telemetry;

/// <summary>Result of executing a single harness query against one engine.</summary>
public record HarnessQueryResult(
    string QueryName,
    bool Succeeded,
    string? ErrorCode,
    NormalizedResult? Result,
    TimeSpan Elapsed,
    QueryTelemetryCapture Telemetry
)
{
    /// <summary>Convenience factory for a successful result.</summary>
    public static HarnessQueryResult Success(
        string name,
        NormalizedResult result,
        TimeSpan elapsed,
        QueryTelemetryCapture telemetry) =>
        new(name, Succeeded: true, ErrorCode: null, Result: result, elapsed, telemetry);

    /// <summary>Convenience factory for a failed result.</summary>
    public static HarnessQueryResult Failure(
        string name,
        string errorCode,
        TimeSpan elapsed,
        QueryTelemetryCapture telemetry) =>
        new(name, Succeeded: false, ErrorCode: errorCode, Result: null, elapsed, telemetry);
}
