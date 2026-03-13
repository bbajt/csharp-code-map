namespace CodeMap.Core.Models;

/// <summary>
/// Records that a budget limit was applied, clamping the requested value to the hard cap.
/// </summary>
public record LimitApplied(
    int Requested,
    int HardCap
);
