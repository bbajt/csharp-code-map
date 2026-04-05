namespace CodeMap.Harness.Comparison;

using System.Text.Json.Serialization;
using CodeMap.Harness.Queries;

/// <summary>
/// Engine-agnostic, comparison-safe canonical form of a query result.
/// All comparisons operate on NormalizedResult, never on raw IQueryEngine output.
/// Suitable for JSON serialization as a golden file.
/// </summary>
public record NormalizedResult(
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    QuerySuiteCategory Category,
    IReadOnlyList<string> SymbolIds,        // stable_ids, sorted ascending
    IReadOnlyList<string> EdgeKeys,         // "from_stable_id→to_stable_id", sorted
    IReadOnlyList<string> FactKeys,         // "kind:primary_value", sorted
    IReadOnlyDictionary<string, string> ScalarFields,   // counts, names, flags
    bool IsTruncated,
    int? TotalAvailable
)
{
    /// <summary>Empty result for a successful query that returned no items.</summary>
    public static NormalizedResult Empty(QuerySuiteCategory category) =>
        new(category,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: [],
            ScalarFields: new Dictionary<string, string>(),
            IsTruncated: false,
            TotalAvailable: 0);
}
