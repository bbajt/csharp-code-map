namespace CodeMap.Mcp.Resolution;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Default resolver used by every symbol-scoped handler. Wraps
/// <see cref="IQueryEngine.SearchSymbolsAsync"/> so a single <c>name</c> argument can stand
/// in for the verbose doc-comment <c>symbol_id</c> when the name is unambiguous.
/// </summary>
public sealed class McpSymbolResolver : IMcpSymbolResolver
{
    /// <summary>
    /// Cap on the number of candidate lines included in an <c>AMBIGUOUS</c> error. Five is
    /// enough for an agent to pick without re-querying, and small enough to fit in a
    /// short error message.
    /// </summary>
    public const int MaxCandidates = 5;

    private readonly IQueryEngine _queryEngine;

    /// <summary>Initializes the resolver with the query engine it delegates name lookups to.</summary>
    public McpSymbolResolver(IQueryEngine queryEngine)
    {
        _queryEngine = queryEngine;
    }

    /// <inheritdoc/>
    public async Task<ResolveResult> ResolveAsync(JsonObject? args, RoutingContext routing, CancellationToken ct)
    {
        // 1. Explicit symbol_id wins, verbatim. Preserves the precise path for callers
        // that already have an ID (e.g. from a prior response's symbol_id field).
        var explicitId = args?["symbol_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(explicitId))
            return new ResolveResult(SymbolId.From(explicitId), null);

        // 2. Name path. Caller supplies `name` and optionally `name_filter` for scope.
        var name = args?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            return new ResolveResult(null, CodeMapError.InvalidArgument(
                "Either 'symbol_id' or 'name' must be provided."));
        }

        var filters = ParseNameFilter(args);

        // Fetch one past the display cap so we can report "X+ candidates" accurately when
        // the actual match count is huge.
        var budgets = new BudgetLimits(maxResults: MaxCandidates + 1);
        var searchResult = await _queryEngine.SearchSymbolsAsync(routing, name, filters, budgets, ct).ConfigureAwait(false);
        if (searchResult.IsFailure)
            return new ResolveResult(null, searchResult.Error);

        var hits = searchResult.Value.Data.Hits;

        if (hits.Count == 0)
        {
            return new ResolveResult(null, new CodeMapError(
                ErrorCodes.NotFound,
                $"No symbol matches name '{name}'. Tip: try a broader query with symbols.search, or drop name_filter."));
        }

        if (hits.Count == 1)
            return new ResolveResult(hits[0].SymbolId, null);

        // Multiple matches: inline the top N as both candidate IDs and human-readable
        // lines. The agent picks one and retries with symbol_id, or narrows name_filter.
        var top = hits.Take(MaxCandidates).ToList();
        var candidateIds = top.Select(h => h.SymbolId.Value).ToList();
        var candidateLines = top.Select(h =>
            $"  {h.SymbolId.Value} — {h.Kind} {h.Signature} ({h.FilePath.Value}:{h.Line})");

        var totalLabel = hits.Count > MaxCandidates
            ? $"{MaxCandidates}+"
            : hits.Count.ToString();

        var message =
            $"Name '{name}' matches {totalLabel} symbols. Pass one of these as symbol_id, " +
            "or narrow with name_filter (namespace / file_path / project_name / kinds):\n"
            + string.Join("\n", candidateLines);

        return new ResolveResult(null, CodeMapError.Ambiguous(message, candidateIds));
    }

    /// <summary>
    /// Parses the optional <c>name_filter</c> object. Uses the same shape as
    /// <see cref="SymbolSearchFilters"/> so callers know exactly what keys are supported.
    /// </summary>
    private static SymbolSearchFilters? ParseNameFilter(JsonObject? args)
    {
        if (args?["name_filter"] is not JsonObject filterObj)
            return null;

        var kindsNode = filterObj["kinds"] as JsonArray;
        List<SymbolKind>? kinds = null;
        if (kindsNode is not null)
        {
            kinds = [];
            foreach (var node in kindsNode)
            {
                var kindStr = node?.GetValue<string>();
                if (kindStr is not null && Enum.TryParse<SymbolKind>(kindStr, ignoreCase: true, out var k))
                    kinds.Add(k);
            }
        }

        var ns = filterObj["namespace"]?.GetValue<string>();
        var filePath = filterObj["file_path"]?.GetValue<string>();
        var projectName = filterObj["project_name"]?.GetValue<string>();

        if (kinds is null && ns is null && filePath is null && projectName is null)
            return null;

        return new SymbolSearchFilters(
            Kinds: kinds?.AsReadOnly(),
            Namespace: ns,
            FilePath: filePath,
            ProjectName: projectName);
    }
}
