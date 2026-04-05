namespace CodeMap.Harness.Queries.Suites;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;

/// <summary>
/// codemap.summarize and codemap.export.
/// Parity rule: counts match; section presence matches.
/// Text content is NOT compared (prose generation may differ).
/// </summary>
public sealed class SummarizeSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "codemap.summarize";
    public QuerySuiteCategory Category => QuerySuiteCategory.SummarizeExport;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.SummarizeAsync(routing, repoPath: repo.RepoRoot, ct: ct),
            ResultNormalizer.FromSummarize);
    }
}

public sealed class ExportStandardSuite(RepoId repoId) : IHarnessQuery
{
    public string Name => "codemap.export.standard";
    public QuerySuiteCategory Category => QuerySuiteCategory.SummarizeExport;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        return QueryHelpers.ExecuteAsync(
            Name, engine, routing,
            () => engine.ExportAsync(routing, detail: "standard", repoPath: repo.RepoRoot, ct: ct),
            ResultNormalizer.FromExport);
    }
}

public static class SummarizeExportSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoId repoId) =>
    [
        new SummarizeSuite(repoId),
        new ExportStandardSuite(repoId),
    ];
}
