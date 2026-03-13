namespace CodeMap.Core.Models;

/// <summary>
/// Per-phase timing data included in every response envelope.
/// All values are in milliseconds.
/// </summary>
public record TimingBreakdown(
    double TotalMs,
    double DbQueryMs = 0,
    double CacheLookupMs = 0,
    double RoslynCompileMs = 0,
    double RankingMs = 0
);
