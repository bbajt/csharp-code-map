namespace CodeMap.TestUtilities.VerifyConfig;

using System.Runtime.CompilerServices;

/// <summary>
/// Global Verify settings. Applied once before any test runs.
/// </summary>
public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Scrub non-deterministic fields from snapshots
        VerifyTests.VerifierSettings.ScrubLinesContaining("timing_ms");
        VerifyTests.VerifierSettings.ScrubLinesContaining("cost_avoided");
    }
}
