namespace CodeMap.Harness.Queries.Suites;

using System.Diagnostics;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Telemetry;

/// <summary>
/// index.diff between two baselines.
/// Skipped if only one baseline exists for the repo.
/// Parity rule: same added/removed/changed stable_id sets; same fact diff key sets.
/// </summary>
public sealed class DiffSuite(RepoId repoId, CommitSha fromCommit, CommitSha toCommit) : IHarnessQuery
{
    public string Name => $"index.diff:{fromCommit.Value[..8]}..{toCommit.Value[..8]}";
    public QuerySuiteCategory Category => QuerySuiteCategory.Diff;
    public bool IncludeInSmoke => false;

    public async Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var sw = Stopwatch.StartNew();

        var result = await engine.DiffAsync(routing, fromCommit, toCommit, ct: ct).ConfigureAwait(false);
        sw.Stop();

        return result.IsSuccess
            ? QueryHelpers.RunQuery(Name, ResultNormalizer.FromDiff(result.Value.Data), sw.Elapsed)
            : QueryHelpers.RunError(Name, result.Error.Code, sw.Elapsed);
    }
}

/// <summary>
/// Placeholder diff suite when no second baseline is available.
/// ExecuteAsync always returns a skipped result (Succeeded=false with SKIPPED code).
/// </summary>
public sealed class DiffSkippedSuite : IHarnessQuery
{
    public string Name => "index.diff.skipped";
    public QuerySuiteCategory Category => QuerySuiteCategory.Diff;
    public bool IncludeInSmoke => false;

    public Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct) =>
        Task.FromResult(HarnessQueryResult.Failure(Name, "SKIPPED", TimeSpan.Zero, new QueryTelemetryCapture()));
}

public static class DiffSuiteFactory
{
    /// <summary>Creates a DiffSuite if two commits are provided, otherwise DiffSkippedSuite.</summary>
    public static IReadOnlyList<IHarnessQuery> Create(RepoId repoId, CommitSha? from, CommitSha? to)
    {
        if (from is null || to is null)
            return [new DiffSkippedSuite()];
        return [new DiffSuite(repoId, from.Value, to.Value)];
    }
}
