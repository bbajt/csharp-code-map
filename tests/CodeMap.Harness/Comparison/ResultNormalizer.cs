namespace CodeMap.Harness.Comparison;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Harness.Queries;

/// <summary>
/// Converts raw IQueryEngine output into canonical NormalizedResult form.
/// All comparisons and golden file saves use this canonical form, never raw output.
///
/// Symbol identity: uses StableId when present, falls back to FQN SymbolId.
/// All collections are sorted to enable order-insensitive comparison.
/// </summary>
public static class ResultNormalizer
{
    public static NormalizedResult FromSymbolSearch(SymbolSearchResponse r)
    {
        var ids = r.Hits
            .Select(h => h.SymbolId.Value)
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.SymbolSearch,
            SymbolIds: ids,
            EdgeKeys: [],
            FactKeys: [],
            ScalarFields: new Dictionary<string, string>
            {
                ["total_count"] = r.TotalCount.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: r.TotalCount);
    }

    public static NormalizedResult FromSymbolCard(SymbolCard card)
    {
        var stableId = card.StableId?.Value ?? card.SymbolId.Value;
        var factKeys = card.Facts
            .Select(f => $"{f.Kind}:{f.Value}")
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.CardAndContext,
            SymbolIds: [card.SymbolId.Value],
            EdgeKeys: [],
            FactKeys: factKeys,
            ScalarFields: new Dictionary<string, string>
            {
                ["fqn"] = card.FullyQualifiedName,
                ["kind"] = card.Kind.ToString(),
                ["stable_id"] = stableId,
                ["span_start"] = card.SpanStart.ToString(),
                ["span_end"] = card.SpanEnd.ToString(),
                ["is_decompiled"] = card.IsDecompiled.ToString().ToLowerInvariant(),
            },
            IsTruncated: false,
            TotalAvailable: null);
    }

    public static NormalizedResult FromCallGraph(CallGraphResponse r, QuerySuiteCategory category)
    {
        var ids = r.Nodes
            .Select(n => n.SymbolId.Value)
            .OrderBy(x => x)
            .ToList();

        var edges = r.Nodes
            .SelectMany(n => n.EdgesTo.Select(to => $"{n.SymbolId.Value}→{to.Value}"))
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            category,
            SymbolIds: ids,
            EdgeKeys: edges,
            FactKeys: [],
            ScalarFields: new Dictionary<string, string>
            {
                ["total_nodes"] = r.TotalNodesFound.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: r.TotalNodesFound);
    }

    public static NormalizedResult FromTypeHierarchy(TypeHierarchyResponse r)
    {
        var ids = new List<string>();
        if (r.BaseType is not null) ids.Add(r.BaseType.SymbolId.Value);
        ids.AddRange(r.Interfaces.Select(i => i.SymbolId.Value));
        ids.AddRange(r.DerivedTypes.Select(d => d.SymbolId.Value));
        ids.Sort();

        return new NormalizedResult(
            QuerySuiteCategory.TypeHierarchy,
            SymbolIds: ids,
            EdgeKeys: [],
            FactKeys: [],
            ScalarFields: new Dictionary<string, string>
            {
                ["target"] = r.TargetType.Value,
                ["base_count"] = (r.BaseType is not null ? 1 : 0).ToString(),
                ["interface_count"] = r.Interfaces.Count.ToString(),
                ["derived_count"] = r.DerivedTypes.Count.ToString(),
            },
            IsTruncated: false,
            TotalAvailable: null);
    }

    public static NormalizedResult FromEndpoints(ListEndpointsResponse r)
    {
        var keys = r.Endpoints
            .Select(e => $"{e.HttpMethod}:{e.RoutePath}")
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.Surfaces,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: keys,
            ScalarFields: new Dictionary<string, string>
            {
                ["total_count"] = r.TotalCount.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: r.TotalCount);
    }

    public static NormalizedResult FromConfigKeys(ListConfigKeysResponse r)
    {
        var keys = r.Keys
            .Select(k => $"{k.Key}|{k.UsagePattern}")
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.Surfaces,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: keys,
            ScalarFields: new Dictionary<string, string>
            {
                ["total_count"] = r.TotalCount.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: r.TotalCount);
    }

    public static NormalizedResult FromDbTables(ListDbTablesResponse r)
    {
        var keys = r.Tables
            .Select(t => string.IsNullOrEmpty(t.Schema) ? t.TableName : $"{t.Schema}.{t.TableName}")
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.Surfaces,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: keys,
            ScalarFields: new Dictionary<string, string>
            {
                ["total_count"] = r.TotalCount.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: r.TotalCount);
    }

    public static NormalizedResult FromTextSearch(SearchTextResponse r)
    {
        var keys = r.Matches
            .Select(m => $"{m.FilePath.Value}:{m.Line}:{m.Excerpt.Trim()}")
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.TextSearch,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: keys,
            ScalarFields: new Dictionary<string, string>
            {
                ["total_files"] = r.TotalFiles.ToString(),
                ["match_count"] = r.Matches.Count.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: r.Matches.Count);
    }

    public static NormalizedResult FromSummarize(SummarizeResponse r)
    {
        return new NormalizedResult(
            QuerySuiteCategory.SummarizeExport,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: r.Sections.Select(s => s.Title).OrderBy(x => x).ToList(),
            ScalarFields: new Dictionary<string, string>
            {
                ["symbol_count"] = r.Stats.SymbolCount.ToString(),
                ["reference_count"] = r.Stats.ReferenceCount.ToString(),
                ["fact_count"] = r.Stats.FactCount.ToString(),
                ["endpoint_count"] = r.Stats.EndpointCount.ToString(),
                ["config_key_count"] = r.Stats.ConfigKeyCount.ToString(),
                ["db_table_count"] = r.Stats.DbTableCount.ToString(),
                ["di_registration_count"] = r.Stats.DiRegistrationCount.ToString(),
                ["section_count"] = r.Sections.Count.ToString(),
            },
            IsTruncated: false,
            TotalAvailable: null);
    }

    public static NormalizedResult FromExport(ExportResponse r)
    {
        return new NormalizedResult(
            QuerySuiteCategory.SummarizeExport,
            SymbolIds: [],
            EdgeKeys: [],
            FactKeys: [],
            ScalarFields: new Dictionary<string, string>
            {
                ["detail_level"] = r.DetailLevel,
                ["symbol_count"] = r.Stats.SymbolCount.ToString(),
                ["reference_count"] = r.Stats.ReferenceCount.ToString(),
                ["fact_count"] = r.Stats.FactCount.ToString(),
                ["truncated"] = r.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: r.Truncated,
            TotalAvailable: null);
    }

    public static NormalizedResult FromDiff(DiffResponse r)
    {
        var symbolKeys = r.SymbolChanges
            .Select(sc =>
            {
                var id = sc.StableId?.Value ?? sc.FromSymbolId?.Value ?? sc.ToSymbolId?.Value ?? "?";
                return $"{sc.ChangeType}:{id}";
            })
            .OrderBy(x => x)
            .ToList();

        var factKeys = r.FactChanges
            .Select(fc => $"{fc.ChangeType}:{fc.Kind}:{fc.FromValue ?? fc.ToValue ?? "?"}")
            .OrderBy(x => x)
            .ToList();

        return new NormalizedResult(
            QuerySuiteCategory.Diff,
            SymbolIds: [],
            EdgeKeys: symbolKeys,
            FactKeys: factKeys,
            ScalarFields: new Dictionary<string, string>
            {
                ["symbols_added"] = r.Stats.SymbolsAdded.ToString(),
                ["symbols_removed"] = r.Stats.SymbolsRemoved.ToString(),
                ["symbols_renamed"] = r.Stats.SymbolsRenamed.ToString(),
                ["endpoints_added"] = r.Stats.EndpointsAdded.ToString(),
                ["endpoints_removed"] = r.Stats.EndpointsRemoved.ToString(),
            },
            IsTruncated: false,
            TotalAvailable: null);
    }
}
