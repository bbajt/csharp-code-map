namespace CodeMap.Harness.Queries.Suites;

using System.Diagnostics;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Telemetry;

/// <summary>
/// Shared helpers for constructing routing contexts and measuring elapsed time.
/// </summary>
internal static class QueryHelpers
{
    public static RoutingContext CommittedRouting(RepoId repoId, CommitSha commitSha) =>
        new(repoId, consistency: ConsistencyMode.Committed, baselineCommitSha: commitSha);

    public static HarnessQueryResult RunQuery(
        string name,
        NormalizedResult result,
        TimeSpan elapsed) =>
        HarnessQueryResult.Success(name, result, elapsed, new QueryTelemetryCapture());

    public static HarnessQueryResult RunError(
        string name,
        string errorCode,
        TimeSpan elapsed) =>
        HarnessQueryResult.Failure(name, errorCode, elapsed, new QueryTelemetryCapture());

    public static async Task<HarnessQueryResult> ExecuteAsync<T>(
        string name,
        IQueryEngine engine,
        RoutingContext routing,
        Func<Task<Result<ResponseEnvelope<T>, CodeMapError>>> query,
        Func<T, NormalizedResult> normalize)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await query().ConfigureAwait(false);
            sw.Stop();
            return result.IsSuccess
                ? RunQuery(name, normalize(result.Value.Data), sw.Elapsed)
                : RunError(name, result.Error.Code, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return RunError(name, ex.Message.Length > 80 ? ex.Message[..80] : ex.Message, sw.Elapsed);
        }
    }
}
