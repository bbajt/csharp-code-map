namespace CodeMap.Harness.Queries.Suites;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Core.Models;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// Symbol search queries: exact match, prefix, CamelCase, kind filter, no-match.
/// Parity rule: same symbol_id set (order-insensitive), same IsTruncated flag.
/// </summary>
public sealed class SymbolSearchSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "Service";

    public string Name => $"symbols.search:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.SymbolSearch;
    public bool IncludeInSmoke => true;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchSymbolsAsync(routing, _term, filters: null, budgets: null, ct),
            r => ResultNormalizer.FromSymbolSearch(r));
    }
}

/// <summary>Prefix search with 3-letter prefix. Smoke=false (large result sets are slower).</summary>
public sealed class SymbolSearchPrefixSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _prefix = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0][..Math.Min(3, repo.KnownQueryInputs[0].Length)]
        : "Ord";

    public string Name => $"symbols.search.prefix:{_prefix}";
    public QuerySuiteCategory Category => QuerySuiteCategory.SymbolSearch;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchSymbolsAsync(routing, _prefix + "*", filters: null, budgets: null, ct),
            r => ResultNormalizer.FromSymbolSearch(r));
    }
}

/// <summary>Kind-filtered search (Methods only).</summary>
public sealed class SymbolSearchByKindSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 1
        ? repo.KnownQueryInputs[1]
        : repo.KnownQueryInputs.Count > 0 ? repo.KnownQueryInputs[0] : "Service";

    public string Name => $"symbols.search.kind:{_term}:Method";
    public QuerySuiteCategory Category => QuerySuiteCategory.SymbolSearch;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var filters = new SymbolSearchFilters(Kinds: [SymbolKind.Method]);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchSymbolsAsync(routing, _term, filters, budgets: null, ct),
            r => ResultNormalizer.FromSymbolSearch(r));
    }
}

/// <summary>No-match query — expect empty result, not error.</summary>
public sealed class SymbolSearchNoMatchSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "symbols.search.no_match:__nonexistent_xyzzy__";
    public QuerySuiteCategory Category => QuerySuiteCategory.SymbolSearch;
    public bool IncludeInSmoke => true;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchSymbolsAsync(routing, "__nonexistent_xyzzy__", filters: null, budgets: null, ct),
            r => ResultNormalizer.FromSymbolSearch(r));
    }
}

/// <summary>Factory: creates all SymbolSearch queries for a repo.</summary>
public static class SymbolSearchSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoDescriptor repo, RepoId repoId) =>
    [
        new SymbolSearchSuite(repo, repoId),
        new SymbolSearchPrefixSuite(repo, repoId),
        new SymbolSearchByKindSuite(repo, repoId),
        new SymbolSearchNoMatchSuite(repoId),
    ];
}
