namespace CodeMap.Query;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Generates human-readable answer text for each query type.
/// </summary>
public static class AnswerGenerator
{
    /// <summary>Generates the answer text for a symbol search result.</summary>
    public static string ForSearch(IReadOnlyList<SymbolSearchHit> hits, string query, bool truncated)
    {
        if (hits.Count == 0)
            return $"No symbols found matching '{query}'.";
        var suffix = truncated ? " (truncated)" : "";
        return $"Found {hits.Count} symbols matching '{query}'{suffix}. Use symbols.get_card for details.";
    }

    /// <summary>Generates the answer text for a symbol card lookup.</summary>
    public static string ForCard(SymbolCard card) =>
        $"{card.Kind} {card.FullyQualifiedName} — {card.Signature}";

    /// <summary>Generates the answer text for a code span read.</summary>
    public static string ForSpan(SpanResponse span) =>
        $"Lines {span.StartLine}–{span.EndLine} of {span.FilePath} ({span.TotalFileLines} total)";

    /// <summary>Generates the answer text for a definition span lookup.</summary>
    public static string ForDefinitionSpan(SymbolCard card, SpanResponse span) =>
        $"Definition of {card.Kind} {card.FullyQualifiedName} — lines {span.StartLine}–{span.EndLine}";

    /// <summary>Generates the answer text for a find-references result.</summary>
    public static string ForFindRefs(SymbolId symbolId, int count, RefKind? kind, bool truncated)
    {
        var kindSuffix = kind is not null ? $" (kind: {kind})" : "";
        var truncSuffix = truncated ? $" (showing first {count})" : "";
        return count == 0
            ? $"No references found for '{symbolId.Value}'{kindSuffix}."
            : $"Found {count} reference{(count == 1 ? "" : "s")} to '{symbolId.Value}'{kindSuffix}{truncSuffix}.";
    }

    /// <summary>Generates the answer text for a call graph traversal (callers or callees).</summary>
    public static string ForCallGraph(SymbolId symbolId, string direction, int nodeCount, int depth, bool truncated)
    {
        var truncSuffix = truncated ? " (truncated)" : "";
        return nodeCount == 0
            ? $"No {direction} found for '{symbolId.Value}' (depth: {depth})."
            : $"Found {nodeCount} {direction} of '{symbolId.Value}' (depth: {depth}){truncSuffix}.";
    }

    /// <summary>Generates the answer text for a type hierarchy query.</summary>
    public static string ForTypeHierarchy(SymbolId symbolId, TypeRef? baseType, int interfaceCount, int derivedCount)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (baseType is not null) parts.Add($"base: {baseType.DisplayName}");
        if (interfaceCount > 0) parts.Add($"{interfaceCount} interface{(interfaceCount == 1 ? "" : "s")}");
        if (derivedCount > 0) parts.Add($"{derivedCount} derived type{(derivedCount == 1 ? "" : "s")}");
        return parts.Count == 0
            ? $"Type '{symbolId.Value}' has no hierarchy relationships."
            : $"Type '{symbolId.Value}' — {string.Join(", ", parts)}.";
    }

    /// <summary>Generates the answer text for an endpoint surface listing.</summary>
    public static string ForEndpoints(int count, string? pathFilter, string? httpMethod, bool truncated)
    {
        if (count == 0) return "No HTTP endpoints found.";
        var filters = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(pathFilter)) filters.Add($"path: '{pathFilter}'");
        if (!string.IsNullOrEmpty(httpMethod)) filters.Add($"method: {httpMethod}");
        var filterSuffix = filters.Count > 0 ? $" ({string.Join(", ", filters)})" : "";
        var truncSuffix = truncated ? " (truncated)" : "";
        return $"Found {count} HTTP endpoint{(count == 1 ? "" : "s")}{filterSuffix}{truncSuffix}.";
    }

    /// <summary>Generates the answer text for a configuration key surface listing.</summary>
    public static string ForConfigKeys(int count, string? keyFilter, bool truncated)
    {
        if (count == 0) return "No configuration keys found.";
        var filterSuffix = !string.IsNullOrEmpty(keyFilter) ? $" (key prefix: '{keyFilter}')" : "";
        var truncSuffix = truncated ? " (truncated)" : "";
        return $"Found {count} configuration key{(count == 1 ? "" : "s")}{filterSuffix}{truncSuffix}.";
    }

    /// <summary>Generates the answer text for a database table surface listing.</summary>
    public static string ForDbTables(int count, string? tableFilter, bool truncated)
    {
        if (count == 0) return "No database tables found.";
        var filterSuffix = !string.IsNullOrEmpty(tableFilter) ? $" (table prefix: '{tableFilter}')" : "";
        var truncSuffix = truncated ? " (truncated)" : "";
        return $"Found {count} database table{(count == 1 ? "" : "s")}{filterSuffix}{truncSuffix}.";
    }

    /// <summary>Generates the answer text for a feature trace result.</summary>
    public static string ForFeatureTrace(string entryPointName, int nodeCount, int depth, bool truncated)
    {
        var truncSuffix = truncated ? " (truncated)" : "";
        return $"Traced feature from '{entryPointName}' across {nodeCount} node{(nodeCount == 1 ? "" : "s")} at depth {depth}{truncSuffix}.";
    }

    /// <summary>Generates the answer text for a codebase summary.</summary>
    public static string ForSummarize(string solutionName, int sectionCount, int factCount)
        => $"Summary of '{solutionName}': {sectionCount} section{(sectionCount == 1 ? "" : "s")}, {factCount} fact{(factCount == 1 ? "" : "s")} indexed.";

    /// <summary>Generates the answer text for a symbol context result.</summary>
    public static string ForContext(string symbolName, int calleeCount, int totalFound)
    {
        var suffix = totalFound > calleeCount ? $" ({totalFound} total found, showing {calleeCount})" : "";
        return calleeCount > 0
            ? $"Context for '{symbolName}': card + code + {calleeCount} callee{(calleeCount == 1 ? "" : "s")}{suffix}."
            : $"Context for '{symbolName}': card + code (no callees found).";
    }

    /// <summary>Generates the answer text for a text search result.</summary>
    public static string ForSearchText(SearchTextResponse r) =>
        r.Matches.Count == 0
            ? $"No matches for `{r.Pattern}` in {r.TotalFiles} indexed files."
            : r.Truncated
                ? $"Found {r.Matches.Count}+ matches for `{r.Pattern}` across {r.TotalFiles} files (results capped — increase limit to see more)."
                : $"Found {r.Matches.Count} match{(r.Matches.Count == 1 ? "" : "es")} for `{r.Pattern}` across {r.TotalFiles} files.";
}
