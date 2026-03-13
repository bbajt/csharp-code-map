namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;

/// <summary>Response payload for the <c>codemap.summarize</c> MCP tool.</summary>
public record SummarizeResponse(
    /// <summary>Inferred solution or repository name.</summary>
    string SolutionName,
    /// <summary>Full markdown-rendered summary, ready to paste into a prompt or save as CLAUDE.md.</summary>
    string Markdown,
    /// <summary>Structured sections for programmatic access.</summary>
    IReadOnlyList<SummarySection> Sections,
    /// <summary>Aggregate metrics about the indexed codebase.</summary>
    SummaryStats Stats
);

/// <summary>A single section of the codebase summary.</summary>
public record SummarySection(
    /// <summary>Section heading, e.g. "API Surface".</summary>
    string Title,
    /// <summary>Markdown content for this section (table, list, or prose).</summary>
    string Content,
    /// <summary>Number of items displayed in this section (may be less than total if capped).</summary>
    int ItemCount,
    /// <summary>True when the section was capped by <c>max_items_per_section</c>; more items exist.</summary>
    bool Truncated = false,
    /// <summary>
    /// Exact total available when the section was not truncated; null when truncated
    /// (use a higher <c>max_items_per_section</c> to retrieve all items).
    /// </summary>
    int? TotalAvailable = null
);

/// <summary>Aggregate metrics derived from the codebase index.</summary>
public record SummaryStats(
    /// <summary>Number of projects detected from stored file metadata.</summary>
    int ProjectCount,
    /// <summary>Total symbol count (sum across all projects).</summary>
    int SymbolCount,
    /// <summary>Total reference count (sum across all projects).</summary>
    int ReferenceCount,
    /// <summary>Total extracted facts across all FactKinds.</summary>
    int FactCount,
    /// <summary>Number of HTTP endpoints extracted.</summary>
    int EndpointCount,
    /// <summary>Number of configuration key usages extracted.</summary>
    int ConfigKeyCount,
    /// <summary>Number of database tables extracted.</summary>
    int DbTableCount,
    /// <summary>Number of DI registrations extracted.</summary>
    int DiRegistrationCount,
    /// <summary>Number of distinct exception types thrown.</summary>
    int ExceptionTypeCount,
    /// <summary>Number of distinct log message templates extracted.</summary>
    int LogTemplateCount,
    /// <summary>Semantic quality of the baseline index.</summary>
    SemanticLevel SemanticLevel
);
