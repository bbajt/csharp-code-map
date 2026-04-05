namespace CodeMap.Harness.Queries.Suites;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// surfaces.list_endpoints, surfaces.list_config_keys, surfaces.list_db_tables.
/// Parity rule: same primary key set (route+method, config key, table name), order-insensitive.
/// </summary>
public sealed class SurfacesEndpointsSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "surfaces.list_endpoints";
    public QuerySuiteCategory Category => QuerySuiteCategory.Surfaces;
    public bool IncludeInSmoke => true;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.ListEndpointsAsync(routing, pathFilter: null, httpMethod: null, limit: 100, ct: ct),
            ResultNormalizer.FromEndpoints);
    }
}

public sealed class SurfacesConfigKeysSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "surfaces.list_config_keys";
    public QuerySuiteCategory Category => QuerySuiteCategory.Surfaces;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.ListConfigKeysAsync(routing, keyFilter: null, limit: 100, ct: ct),
            ResultNormalizer.FromConfigKeys);
    }
}

public sealed class SurfacesDbTablesSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "surfaces.list_db_tables";
    public QuerySuiteCategory Category => QuerySuiteCategory.Surfaces;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.ListDbTablesAsync(routing, tableFilter: null, limit: 100, ct: ct),
            ResultNormalizer.FromDbTables);
    }
}

public static class SurfacesSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoId repoId) =>
    [
        new SurfacesEndpointsSuite(repoId),
        new SurfacesConfigKeysSuite(repoId),
        new SurfacesDbTablesSuite(repoId),
    ];
}
