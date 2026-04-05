namespace CodeMap.Harness.Reports;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Repos;

/// <summary>
/// Accumulates harness results and writes a JSON report to a file.
/// Used by GoldenRunner to serialize NormalizedResult golden files,
/// and by CI to produce machine-readable run reports.
/// </summary>
public sealed class JsonReporter(string? outputPath = null) : IHarnessReporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly List<JsonObject> _results = [];
    private int _passed, _failed, _skipped;

    public void ReportIndexStart(RepoDescriptor repo) { }
    public void ReportIndexComplete(RepoDescriptor repo, TimeSpan elapsed, bool alreadyExisted) { }
    public void ReportIndexFailed(RepoDescriptor repo, string message) { }
    public void ReportAnchorCheck(RepoDescriptor repo, bool allPassed, int total) { }

    public void ReportQueryResult(IHarnessQuery query, PairResult result)
    {
        var status = result.IsPass ? "pass" : result switch
        {
            PairResult.LeftError or PairResult.RightError or PairResult.BothError => "error",
            _ => "fail",
        };

        if (result.IsPass) _passed++;
        else if (result is PairResult.LeftError or PairResult.RightError or PairResult.BothError) _skipped++;
        else _failed++;

        var obj = new JsonObject
        {
            ["query"] = query.Name,
            ["category"] = query.Category.ToString(),
            ["status"] = status,
        };

        if (result is PairResult.Mismatch mm)
        {
            obj["differences"] = JsonSerializer.SerializeToNode(
                mm.Differences.Select(d => new { d.FieldName, d.LeftValue, d.RightValue }),
                JsonOpts);
        }
        else if (result is PairResult.GoldenMismatch gm)
        {
            obj["differences"] = JsonSerializer.SerializeToNode(
                gm.Differences.Select(d => new { d.FieldName, d.LeftValue, d.RightValue }),
                JsonOpts);
        }

        _results.Add(obj);
    }

    public void ReportSummary(int passed, int failed, int skipped, TimeSpan totalElapsed)
    {
        if (outputPath is null) return;

        var report = new JsonObject
        {
            ["passed"] = _passed,
            ["failed"] = _failed,
            ["skipped"] = _skipped,
            ["total_elapsed_ms"] = totalElapsed.TotalMilliseconds,
            ["results"] = new JsonArray(_results.Select(r => (JsonNode)r).ToArray()),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, report.ToJsonString(JsonOpts));
    }

    /// <summary>Serializes a single NormalizedResult to JSON for a golden file.</summary>
    public static string SerializeGolden(NormalizedResult result) =>
        JsonSerializer.Serialize(result, JsonOpts);

    /// <summary>Deserializes a NormalizedResult from a golden file.</summary>
    public static NormalizedResult? DeserializeGolden(string json) =>
        JsonSerializer.Deserialize<NormalizedResult>(json, JsonOpts);
}
