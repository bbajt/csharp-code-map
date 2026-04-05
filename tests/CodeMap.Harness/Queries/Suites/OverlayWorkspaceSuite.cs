namespace CodeMap.Harness.Queries.Suites;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// Overlay workspace queries: search in workspace mode returns same results as committed mode.
/// Parity rule: overlay committed-mode queries follow the same rules as their non-overlay equivalents.
/// Phase 1: reads committed baseline only (no overlay delta).
/// Full overlay mutation tests added in later phases.
/// </summary>
public sealed class OverlaySearchSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "Service";

    public string Name => $"overlay.symbols.search:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.OverlayWorkspace;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        // Phase 1: use committed routing — no workspace created here.
        // Full overlay tests (create workspace → mutate → query → verify) added in Phase 3+.
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SearchSymbolsAsync(routing, _term, filters: null, budgets: null, ct),
            r => ResultNormalizer.FromSymbolSearch(r));
    }
}

public static class OverlayWorkspaceSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoDescriptor repo, RepoId repoId) =>
    [
        new OverlaySearchSuite(repo, repoId),
    ];
}
