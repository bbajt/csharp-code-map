namespace CodeMap.Harness.Comparison;

using System.Diagnostics;

using CodeMap.Harness.Queries;

/// <summary>
/// Semantic equality comparison between two NormalizedResults for the same query.
/// Parity rules per QuerySuiteCategory are documented in HARNESS-DESIGN.MD §8.
/// </summary>
public static class QueryComparator
{
    public static PairResult Compare(
        IHarnessQuery query,
        HarnessQueryResult left,
        HarnessQueryResult right)
    {
        // Error cases
        if (!left.Succeeded && !right.Succeeded)
            return new PairResult.BothError(left.ErrorCode!, right.ErrorCode!);
        if (!left.Succeeded)
            return new PairResult.LeftError(left.ErrorCode!);
        if (!right.Succeeded)
            return new PairResult.RightError(right.ErrorCode!);

        var ln = left.Result!;
        var rn = right.Result!;
        var telemetry = new TelemetryComparison(left.Elapsed, right.Elapsed);

        var diffs = query.Category switch
        {
            QuerySuiteCategory.SymbolSearch     => CompareSymbolSets(ln, rn),
            QuerySuiteCategory.CardAndContext   => CompareCard(ln, rn),
            QuerySuiteCategory.GraphTraversal   => CompareEdgeSets(ln, rn),
            QuerySuiteCategory.TypeHierarchy    => CompareHierarchy(ln, rn),
            QuerySuiteCategory.Surfaces         => CompareFactKeys(ln, rn),
            QuerySuiteCategory.TextSearch       => CompareTextMatches(ln, rn),
            QuerySuiteCategory.SummarizeExport  => CompareCounts(ln, rn),
            QuerySuiteCategory.Diff             => CompareDiff(ln, rn),
            QuerySuiteCategory.OverlayWorkspace => CompareSymbolSets(ln, rn),
            _ => throw new UnreachableException($"Unhandled category: {query.Category}"),
        };

        return diffs.Count == 0
            ? new PairResult.Match(telemetry)
            : new PairResult.Mismatch(diffs, telemetry);
    }

    /// <summary>Compare a current result against a saved golden NormalizedResult.</summary>
    public static PairResult CompareWithGolden(
        IHarnessQuery query,
        HarnessQueryResult current,
        NormalizedResult golden)
    {
        if (!current.Succeeded)
            return new PairResult.RightError(current.ErrorCode!);

        var actual = current.Result!;
        var telemetry = TelemetryComparison.SingleEngine(current.Elapsed);

        var diffs = query.Category switch
        {
            QuerySuiteCategory.SymbolSearch     => CompareSymbolSets(golden, actual),
            QuerySuiteCategory.CardAndContext   => CompareCard(golden, actual),
            QuerySuiteCategory.GraphTraversal   => CompareEdgeSets(golden, actual),
            QuerySuiteCategory.TypeHierarchy    => CompareHierarchy(golden, actual),
            QuerySuiteCategory.Surfaces         => CompareFactKeys(golden, actual),
            QuerySuiteCategory.TextSearch       => CompareTextMatches(golden, actual),
            QuerySuiteCategory.SummarizeExport  => CompareCounts(golden, actual),
            QuerySuiteCategory.Diff             => CompareDiff(golden, actual),
            QuerySuiteCategory.OverlayWorkspace => CompareSymbolSets(golden, actual),
            _ => throw new UnreachableException($"Unhandled category: {query.Category}"),
        };

        return diffs.Count == 0
            ? new PairResult.GoldenMatch(telemetry)
            : new PairResult.GoldenMismatch(diffs, telemetry);
    }

    // ── Per-category comparison rules ─────────────────────────────────────────

    private static List<FieldDiff> CompareSymbolSets(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareSetField(diffs, "symbol_ids", expected.SymbolIds, actual.SymbolIds);
        CompareScalar(diffs, "truncated", expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    private static List<FieldDiff> CompareCard(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareScalar(diffs, "fqn", expected.ScalarFields, actual.ScalarFields);
        CompareScalar(diffs, "kind", expected.ScalarFields, actual.ScalarFields);
        CompareSetField(diffs, "fact_keys", expected.FactKeys, actual.FactKeys);
        return diffs;
    }

    private static List<FieldDiff> CompareEdgeSets(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareSetField(diffs, "symbol_ids", expected.SymbolIds, actual.SymbolIds);
        CompareSetField(diffs, "edge_keys", expected.EdgeKeys, actual.EdgeKeys);
        CompareScalar(diffs, "truncated", expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    private static List<FieldDiff> CompareHierarchy(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareSetField(diffs, "symbol_ids", expected.SymbolIds, actual.SymbolIds);
        CompareScalar(diffs, "base_count", expected.ScalarFields, actual.ScalarFields);
        CompareScalar(diffs, "interface_count", expected.ScalarFields, actual.ScalarFields);
        CompareScalar(diffs, "derived_count", expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    private static List<FieldDiff> CompareFactKeys(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareSetField(diffs, "fact_keys", expected.FactKeys, actual.FactKeys);
        CompareScalar(diffs, "truncated", expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    private static List<FieldDiff> CompareTextMatches(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareSetField(diffs, "fact_keys", expected.FactKeys, actual.FactKeys);
        CompareScalar(diffs, "match_count", expected.ScalarFields, actual.ScalarFields);
        CompareScalar(diffs, "truncated", expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    private static List<FieldDiff> CompareCounts(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        var countFields = new[] { "symbol_count", "reference_count", "fact_count", "section_count" };
        foreach (var field in countFields)
            CompareScalar(diffs, field, expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    private static List<FieldDiff> CompareDiff(NormalizedResult expected, NormalizedResult actual)
    {
        var diffs = new List<FieldDiff>();
        CompareSetField(diffs, "edge_keys", expected.EdgeKeys, actual.EdgeKeys);
        CompareSetField(diffs, "fact_keys", expected.FactKeys, actual.FactKeys);
        var statsFields = new[] { "symbols_added", "symbols_removed", "symbols_renamed" };
        foreach (var field in statsFields)
            CompareScalar(diffs, field, expected.ScalarFields, actual.ScalarFields);
        return diffs;
    }

    // ── Comparison helpers ─────────────────────────────────────────────────────

    private static void CompareSetField(
        List<FieldDiff> diffs,
        string fieldName,
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        var expectedSet = expected.ToHashSet();
        var actualSet = actual.ToHashSet();

        var missing = expectedSet.Except(actualSet).OrderBy(x => x).ToList();
        var extra = actualSet.Except(expectedSet).OrderBy(x => x).ToList();

        if (missing.Count > 0 || extra.Count > 0)
        {
            var leftStr = missing.Count > 0 ? $"missing: [{string.Join(", ", missing.Take(5))}]" : "ok";
            var rightStr = extra.Count > 0 ? $"extra: [{string.Join(", ", extra.Take(5))}]" : "ok";
            diffs.Add(new FieldDiff(fieldName, leftStr, rightStr));
        }
    }

    private static void CompareScalar(
        List<FieldDiff> diffs,
        string fieldName,
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        var leftVal = expected.GetValueOrDefault(fieldName, "(absent)");
        var rightVal = actual.GetValueOrDefault(fieldName, "(absent)");
        if (!string.Equals(leftVal, rightVal, StringComparison.Ordinal))
            diffs.Add(new FieldDiff(fieldName, leftVal, rightVal));
    }
}
