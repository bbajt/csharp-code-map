namespace CodeMap.Harness.Comparison;

/// <summary>
/// Discriminated union representing the comparison outcome for a single query pair.
/// </summary>
public abstract record PairResult
{
    /// <summary>Both engines returned equivalent results.</summary>
    public record Match(TelemetryComparison Telemetry) : PairResult;

    /// <summary>Both engines returned results, but they differ in one or more fields.</summary>
    public record Mismatch(
        IReadOnlyList<FieldDiff> Differences,
        TelemetryComparison Telemetry) : PairResult;

    /// <summary>Left engine (SQLite) returned an error; right did not.</summary>
    public record LeftError(string ErrorCode) : PairResult;

    /// <summary>Right engine (custom) returned an error; left did not.</summary>
    public record RightError(string ErrorCode) : PairResult;

    /// <summary>Both engines returned errors (may or may not be the same error).</summary>
    public record BothError(string LeftCode, string RightCode) : PairResult;

    /// <summary>Convenience: single-engine golden check — matches saved golden file.</summary>
    public record GoldenMatch(TelemetryComparison Telemetry) : PairResult;

    /// <summary>Convenience: single-engine golden check — diverges from saved golden file.</summary>
    public record GoldenMismatch(
        IReadOnlyList<FieldDiff> Differences,
        TelemetryComparison Telemetry) : PairResult;

    /// <summary>Returns true when the query result is considered passing.</summary>
    public bool IsPass => this is Match or GoldenMatch;
}

/// <summary>A single field-level difference between two query results.</summary>
public record FieldDiff(
    string FieldName,
    string LeftValue,   // SQLite or saved golden
    string RightValue   // Custom or current result
);
