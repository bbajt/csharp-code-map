namespace CodeMap.Harness.Runners;

/// <summary>
/// Exit codes for the harness CLI.
/// Design: HARNESS-DESIGN.MD §12 (C-033).
/// </summary>
public enum HarnessExitCode
{
    Success = 0,
    CorrectnessMismatch = 1,
    KpiMiss = 2,
    TelemetryError = 3,
    ConfigurationError = 4,
    IndexBuildFailure = 5,
}
