namespace CodeMap.Harness.Concurrency;

/// <summary>
/// Keeps a concurrency run from exhausting the machine: under topology T1 memory grows about
/// linearly with the agent count (F1), so a large solution at N=16 can need more RAM than exists.
/// Projections use GiB (2^30 bytes) and only runs of the same repo (PHASE-21-06 T01).
/// </summary>
public static class MemoryGuard
{
    private const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

    /// <summary>Share of physical memory a run may be projected to use.</summary>
    public const double DefaultBudgetFraction = 0.75;

    /// <summary>Default budget in GiB: <see cref="DefaultBudgetFraction"/> of physical RAM.</summary>
    public static double DefaultBudgetGb(long totalPhysicalBytes) =>
        totalPhysicalBytes * DefaultBudgetFraction / BytesPerGb;

    /// <summary>
    /// Projected peak (GiB) for <paramref name="agents"/> processes: the largest per-process peak
    /// working set measured in any run of the same repo, times <paramref name="agents"/>.
    /// Null when nothing has been measured yet, so the first agent count always runs.
    /// </summary>
    public static double? ProjectPeakGb(IReadOnlyList<ConcurrencyRunResult> measuredSameRepo, int agents)
    {
        var worst = measuredSameRepo
            .SelectMany(r => r.Memory.PerProcessPeakWorkingSetBytes)
            .DefaultIfEmpty(0)
            .Max();
        return worst <= 0 ? null : worst * (double)agents / BytesPerGb;
    }
}
