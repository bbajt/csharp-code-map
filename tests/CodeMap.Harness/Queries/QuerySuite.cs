namespace CodeMap.Harness.Queries;

using CodeMap.Harness.Repos;

/// <summary>
/// An ordered collection of harness queries for a specific repo.
/// </summary>
public sealed class QuerySuite(RepoDescriptor repo, IReadOnlyList<IHarnessQuery> queries)
{
    public RepoDescriptor Repo => repo;
    public IReadOnlyList<IHarnessQuery> Queries => queries;

    /// <summary>Returns only the queries with IncludeInSmoke = true.</summary>
    public IReadOnlyList<IHarnessQuery> SmokeQueries =>
        queries.Where(q => q.IncludeInSmoke).ToList();
}
