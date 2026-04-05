namespace CodeMap.Harness.Queries.Suites;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// code.search_text with literal and regex patterns.
/// Parity rule: same (file_path, line, excerpt) tuple set.
/// </summary>
public sealed class TextSearchLiteralSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    // Use a term from KnownQueryInputs as the literal search term
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "class";

    public string Name => $"code.search_text.literal:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.TextSearch;
    public bool IncludeInSmoke => true;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchTextAsync(routing, pattern: _term, filePathFilter: null, budgets: null, ct),
            ResultNormalizer.FromTextSearch);
    }
}

public sealed class TextSearchNoMatchSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "code.search_text.no_match:__xyzzy_nonexistent__";
    public QuerySuiteCategory Category => QuerySuiteCategory.TextSearch;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchTextAsync(routing, pattern: "__xyzzy_nonexistent__", filePathFilter: null, budgets: null, ct),
            ResultNormalizer.FromTextSearch);
    }
}

public static class TextSearchSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoDescriptor repo, RepoId repoId) =>
    [
        new TextSearchLiteralSuite(repo, repoId),
        new TextSearchNoMatchSuite(repoId),
    ];
}
