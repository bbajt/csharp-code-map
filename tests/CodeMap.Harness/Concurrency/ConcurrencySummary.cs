namespace CodeMap.Harness.Concurrency;

using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeMap.Core.Models;

/// <summary>
/// Turns concurrency reports into one Markdown table, so the numbers in the docs are generated
/// rather than hand-transcribed (PHASE-21-06 T01). Output is deterministic: rows are sorted by
/// repo, agent count and workspace mode; all formatting is culture-invariant.
/// </summary>
public static class ConcurrencySummary
{
    private const string None = "—";
    private const double BytesPerMb = 1024.0 * 1024.0;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The latency columns: (header, canonical tool name).</summary>
    private static readonly (string Header, string Tool)[] LatencyColumns =
    [
        ("search", ToolNames.SymbolsSearch),
        ("get_card", ToolNames.SymbolsGetCard),
        ("refs_find", ToolNames.RefsFind),
        ("refresh", ToolNames.IndexRefreshOverlay),
    ];

    /// <summary>
    /// True when a report's tool name is <paramref name="canonical"/> — reports written before v2.9.0
    /// (e.g. the committed Phase 0 reports) record the deprecated dotted names (ADR-051).
    /// </summary>
    private static bool IsTool(string recorded, string canonical) =>
        recorded == canonical
        || (ToolNames.LegacyAliases.TryGetValue(recorded, out var mapped) && mapped == canonical);

    /// <summary>
    /// Parses a report file's JSON. Refuses any schema other than
    /// <see cref="ConcurrencyReport.SchemaV1"/>.
    /// </summary>
    public static (ConcurrencyReport? Report, string? Error) Parse(string json)
    {
        ConcurrencyReport? report;
        try
        {
            report = JsonSerializer.Deserialize<ConcurrencyReport>(json, ConcurrencyJson.Options);
        }
        catch (JsonException ex)
        {
            return (null, $"not a concurrency report: {ex.Message}");
        }

        if (report is null) return (null, "not a concurrency report: empty document");
        if (report.Schema != ConcurrencyReport.SchemaV1)
            return (null, $"unsupported schema '{report.Schema}' (expected '{ConcurrencyReport.SchemaV1}')");
        return (report with { Runs = report.Runs ?? [] }, null);
    }

    /// <summary>Renders every run and skipped entry across <paramref name="reports"/> as one table.</summary>
    public static string ToMarkdown(IEnumerable<ConcurrencyReport> reports)
    {
        var list = reports.ToList();
        var runs = list.SelectMany(r => r.Runs).ToList();
        var skips = list.SelectMany(r => r.Skipped ?? []).ToList();
        var failures = list.SelectMany(r => r.Failed ?? []).ToList();

        var rows = runs
            .Select(r => (Repo: r.Environment.RepoName, N: r.Agents, Mode: r.WorkspaceMode, Line: RunRow(r, runs)))
            .Concat(skips.Select(s => (Repo: s.RepoName, N: s.Agents, Mode: None, Line: SkipRow(s))))
            .Concat(failures.Select(f => (Repo: f.RepoName, N: f.Agents, Mode: None,
                Line: NotRunRow(f.RepoName, f.Agents, $"FAILED: {OneCell(f.Error)}"))))
            .OrderBy(x => x.Repo, StringComparer.Ordinal)
            .ThenBy(x => x.N)
            .ThenBy(x => x.Mode, StringComparer.Ordinal)
            .ThenBy(x => x.Line, StringComparer.Ordinal);

        var header = new List<string> { "Repo", "Commit", "OS", "N", "Mode", "Calls/s", "Calls/s/agent" };
        header.AddRange(LatencyColumns.Select(c => $"{c.Header} p50/p95"));
        header.AddRange(["Peak WS total MB", "×N=1", "Peak WS/proc MB", "Builds/req", "Setup fail", "Errors", "Passed"]);

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", header.Count))).Append('\n');
        foreach (var row in rows) sb.Append(row.Line).Append('\n');
        return sb.ToString();
    }

    private static string RunRow(ConcurrencyRunResult r, IReadOnlyList<ConcurrencyRunResult> all)
    {
        var baselineN1 = all.FirstOrDefault(o =>
            o.Agents == 1
            && o.Environment.RepoName == r.Environment.RepoName
            && o.Environment.Os == r.Environment.Os
            && o.WorkspaceMode == r.WorkspaceMode);
        var ratio = baselineN1 is { Memory.PeakTotalWorkingSetBytes: > 0 }
            ? (r.Memory.PeakTotalWorkingSetBytes / (double)baselineN1.Memory.PeakTotalWorkingSetBytes).ToString("0.00", Inv)
            : None;
        var perProcess = r.Memory.PerProcessPeakWorkingSetBytes.DefaultIfEmpty(0).Max();
        var errors = r.ErrorsByCode.Count == 0
            ? "0"
            : string.Join(", ", r.ErrorsByCode.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}:{e.Value}"));

        var cells = new List<string>
        {
            r.Environment.RepoName,
            r.Environment.CommitSha.Length >= 8 ? r.Environment.CommitSha[..8] : r.Environment.CommitSha,
            r.Environment.Os,
            r.Agents.ToString(Inv),
            r.WorkspaceMode,
            r.CallsPerSecond.ToString("0.0", Inv),
            (r.Agents > 0 ? r.CallsPerSecond / r.Agents : 0).ToString("0.0", Inv),
        };
        foreach (var (_, tool) in LatencyColumns)
        {
            var t = r.Tools.FirstOrDefault(x => IsTool(x.Tool, tool));
            cells.Add(t is null || t.Calls == 0 ? None : string.Create(Inv, $"{t.P50Ms:0.0}/{t.P95Ms:0.0}"));
        }
        cells.AddRange(
        [
            Mb(r.Memory.PeakTotalWorkingSetBytes),
            ratio,
            Mb(perProcess),
            string.Create(Inv, $"{r.Setup.BaselineBuilds}/{r.Setup.BaselineRequests}"),
            r.Setup.AgentsFailedSetup.ToString(Inv),
            errors,
            r.Passed ? "yes" : "no",
        ]);
        return "| " + string.Join(" | ", cells) + " |";
    }

    private static string SkipRow(SkippedRun s) => NotRunRow(s.RepoName, s.Agents, $"SKIPPED: {OneCell(s.Reason)}");

    /// <summary>A row for a run with no numbers: dashes everywhere but repo, N and the last cell.</summary>
    private static string NotRunRow(string repo, int agents, string status)
    {
        var cells = new List<string> { repo, None, None, agents.ToString(Inv) };
        cells.AddRange(Enumerable.Repeat(None, 3 + LatencyColumns.Length + 6));
        cells.Add(status);
        return "| " + string.Join(" | ", cells) + " |";
    }

    /// <summary>Makes free text safe for one table cell: one line, pipes escaped.</summary>
    private static string OneCell(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static string Mb(long bytes) => (bytes / BytesPerMb).ToString("0", Inv);
}
