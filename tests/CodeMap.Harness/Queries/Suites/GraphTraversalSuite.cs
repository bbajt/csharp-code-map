namespace CodeMap.Harness.Queries.Suites;

using System.Diagnostics;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Core.Models;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// graph.callers, graph.callees, graph.trace_feature.
/// Parity rule: same stable_id set, order-insensitive, same IsTruncated flag.
/// </summary>
public sealed class GraphCallersSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 1
        ? repo.KnownQueryInputs[1]
        : repo.KnownQueryInputs.Count > 0 ? repo.KnownQueryInputs[0] : "Service";

    // Use anchor kind to filter search — eliminates BM25 vs custom ranking divergence
    private readonly SymbolKind? _anchorKind = repo.Anchors.Count > 1 ? repo.Anchors[1].Kind : null;

    public string Name => $"graph.callers:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.GraphTraversal;
    public bool IncludeInSmoke => true;

    public async Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var sw = Stopwatch.StartNew();

        var filters = _anchorKind.HasValue ? new SymbolSearchFilters(Kinds: [_anchorKind.Value]) : null;
        var searchResult = await engine.SearchSymbolsAsync(routing, _term, filters, budgets: null, ct)
            .ConfigureAwait(false);
        if (!searchResult.IsSuccess || searchResult.Value.Data.Hits.Count == 0)
        {
            sw.Stop();
            return QueryHelpers.RunError(Name, "NOT_FOUND", sw.Elapsed);
        }

        var symbolId = searchResult.Value.Data.Hits[0].SymbolId;
        var result = await engine.GetCallersAsync(routing, symbolId, depth: 2, limitPerLevel: 20, budgets: null, ct: ct)
            .ConfigureAwait(false);
        sw.Stop();

        return result.IsSuccess
            ? QueryHelpers.RunQuery(Name, ResultNormalizer.FromCallGraph(result.Value.Data, Category), sw.Elapsed)
            : QueryHelpers.RunError(Name, result.Error.Code, sw.Elapsed);
    }
}

public sealed class GraphCalleesSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "Service";

    public string Name => $"graph.callees:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.GraphTraversal;
    public bool IncludeInSmoke => false;

    public async Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var sw = Stopwatch.StartNew();

        var searchResult = await engine.SearchSymbolsAsync(routing, _term, filters: null, budgets: null, ct)
            .ConfigureAwait(false);
        if (!searchResult.IsSuccess || searchResult.Value.Data.Hits.Count == 0)
        {
            sw.Stop();
            return QueryHelpers.RunError(Name, "NOT_FOUND", sw.Elapsed);
        }

        var symbolId = searchResult.Value.Data.Hits[0].SymbolId;
        var result = await engine.GetCalleesAsync(routing, symbolId, depth: 2, limitPerLevel: 20, budgets: null, ct: ct)
            .ConfigureAwait(false);
        sw.Stop();

        return result.IsSuccess
            ? QueryHelpers.RunQuery(Name, ResultNormalizer.FromCallGraph(result.Value.Data, Category), sw.Elapsed)
            : QueryHelpers.RunError(Name, result.Error.Code, sw.Elapsed);
    }
}

public sealed class GraphTraceFeatureSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "Service";

    public string Name => $"graph.trace_feature:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.GraphTraversal;
    public bool IncludeInSmoke => false;

    public async Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var sw = Stopwatch.StartNew();

        var searchResult = await engine.SearchSymbolsAsync(routing, _term, filters: null, budgets: null, ct)
            .ConfigureAwait(false);
        if (!searchResult.IsSuccess || searchResult.Value.Data.Hits.Count == 0)
        {
            sw.Stop();
            return QueryHelpers.RunError(Name, "NOT_FOUND", sw.Elapsed);
        }

        var symbolId = searchResult.Value.Data.Hits[0].SymbolId;
        var result = await engine.TraceFeatureAsync(routing, symbolId, depth: 3, limit: 50, ct: ct)
            .ConfigureAwait(false);
        sw.Stop();

        if (!result.IsSuccess)
            return QueryHelpers.RunError(Name, result.Error.Code, sw.Elapsed);

        // Normalize trace: flatten all node stable_ids
        var trace = result.Value.Data;
        var ids = trace.Nodes
            .SelectMany(FlattenNode)
            .Select(n => n.SymbolId.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var normalized = new NormalizedResult(
            QuerySuiteCategory.GraphTraversal,
            SymbolIds: ids,
            EdgeKeys: [],
            FactKeys: [],
            ScalarFields: new Dictionary<string, string>
            {
                ["total_nodes"] = trace.TotalNodesTraversed.ToString(),
                ["truncated"] = trace.Truncated.ToString().ToLowerInvariant(),
            },
            IsTruncated: trace.Truncated,
            TotalAvailable: trace.TotalNodesTraversed);

        return QueryHelpers.RunQuery(Name, normalized, sw.Elapsed);
    }

    private static IEnumerable<CodeMap.Core.Models.TraceNode> FlattenNode(CodeMap.Core.Models.TraceNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var n in FlattenNode(child))
            yield return n;
    }
}

public static class GraphTraversalSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoDescriptor repo, RepoId repoId) =>
    [
        new GraphCallersSuite(repo, repoId),
        new GraphCalleesSuite(repo, repoId),
        new GraphTraceFeatureSuite(repo, repoId),
    ];
}
