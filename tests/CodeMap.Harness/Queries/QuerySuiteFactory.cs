namespace CodeMap.Harness.Queries;

using CodeMap.Core.Types;
using CodeMap.Harness.Queries.Suites;
using CodeMap.Harness.Repos;

/// <summary>Assembles the full query suite for a given repo.</summary>
public static class QuerySuiteFactory
{
    /// <summary>
    /// Builds the ordered list of all harness queries for a repo.
    /// All 9 suite categories are represented.
    /// </summary>
    /// <param name="repo">The repo descriptor.</param>
    /// <param name="repoId">The derived repo identity (from IGitService.GetRepoIdentityAsync).</param>
    /// <param name="diffFrom">Optional FROM commit SHA for the diff suite.</param>
    /// <param name="diffTo">Optional TO commit SHA for the diff suite.</param>
    public static QuerySuite Build(
        RepoDescriptor repo,
        RepoId repoId,
        CommitSha? diffFrom = null,
        CommitSha? diffTo = null)
    {
        var queries = new List<IHarnessQuery>();

        queries.AddRange(SymbolSearchSuiteFactory.Create(repo, repoId));
        queries.AddRange(CardAndContextSuiteFactory.Create(repo, repoId));
        queries.AddRange(GraphTraversalSuiteFactory.Create(repo, repoId));
        queries.AddRange(TypeHierarchySuiteFactory.Create(repo, repoId));
        queries.AddRange(SurfacesSuiteFactory.Create(repoId));
        queries.AddRange(TextSearchSuiteFactory.Create(repo, repoId));
        queries.AddRange(SummarizeExportSuiteFactory.Create(repoId));
        queries.AddRange(DiffSuiteFactory.Create(repoId, diffFrom, diffTo));
        queries.AddRange(OverlayWorkspaceSuiteFactory.Create(repo, repoId));

        return new QuerySuite(repo, queries);
    }
}
