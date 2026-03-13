namespace CodeMap.Core.Models;

/// <summary>Response payload for the <c>codemap.export</c> MCP tool.</summary>
public record ExportResponse(
    /// <summary>Exported content — markdown string or JSON string depending on <see cref="Format"/>.</summary>
    string Content,
    /// <summary>Output format: "markdown" or "json".</summary>
    string Format,
    /// <summary>Detail level used: "summary", "standard", or "full".</summary>
    string DetailLevel,
    /// <summary>Approximate token count of <see cref="Content"/> using text.Length / 4 heuristic.</summary>
    int EstimatedTokens,
    /// <summary>True when <see cref="Content"/> was cut short by the token budget.</summary>
    bool Truncated,
    /// <summary>Aggregate index metrics — same as <see cref="SummaryStats"/> from <c>codemap.summarize</c>.</summary>
    SummaryStats Stats
);
