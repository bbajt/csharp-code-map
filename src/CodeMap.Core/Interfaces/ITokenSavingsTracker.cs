namespace CodeMap.Core.Interfaces;

/// <summary>
/// Tracks token savings across queries. Persists running totals.
/// </summary>
public interface ITokenSavingsTracker
{
    /// <summary>Records tokens saved for a single query.</summary>
    void RecordSaving(int tokensSaved, Dictionary<string, decimal> costAvoided);

    /// <summary>Gets the running session total of tokens saved.</summary>
    long TotalTokensSaved { get; }

    /// <summary>Gets the running session total of cost avoided by model.</summary>
    IReadOnlyDictionary<string, decimal> TotalCostAvoided { get; }
}
