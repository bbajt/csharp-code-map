namespace CodeMap.Harness.Queries.Suites;

using System.Diagnostics;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// types.hierarchy for known type symbols.
/// Parity rule: same base stable_id set + same derived stable_id set.
/// </summary>
public sealed class TypeHierarchySuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "Service";

    public string Name => $"types.hierarchy:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.TypeHierarchy;
    public bool IncludeInSmoke => true;

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
        var result = await engine.GetTypeHierarchyAsync(routing, symbolId, ct).ConfigureAwait(false);
        sw.Stop();

        return result.IsSuccess
            ? QueryHelpers.RunQuery(Name, ResultNormalizer.FromTypeHierarchy(result.Value.Data), sw.Elapsed)
            : QueryHelpers.RunError(Name, result.Error.Code, sw.Elapsed);
    }
}

public static class TypeHierarchySuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoDescriptor repo, RepoId repoId) =>
    [
        new TypeHierarchySuite(repo, repoId),
    ];
}
