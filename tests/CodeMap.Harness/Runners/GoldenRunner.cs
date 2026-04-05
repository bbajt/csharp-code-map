namespace CodeMap.Harness.Runners;

using CodeMap.Core.Interfaces;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;

/// <summary>
/// Saves SQLite query results as golden files, and checks current results against them.
///
/// Golden file layout:
///   tests/CodeMap.Harness/golden/{RepoName}/{query-name-safe}.json
///
/// Save flow: index → run all queries → normalize → serialize to golden/*.json
/// Check flow: run all queries → normalize → compare against saved golden files
/// </summary>
public sealed class GoldenRunner(
    IQueryEngine engine,
    HarnessIndexer indexer,
    string goldenBaseDir)
{
    public async Task<int> SaveAsync(
        RepoDescriptor repo,
        bool force,
        bool confirm,
        IHarnessReporter reporter,
        CancellationToken ct)
    {
        reporter.ReportIndexStart(repo);
        var (repoId, commitShaNullable, alreadyExisted) = await indexer.IndexRepoAsync(repo, reporter, ct).ConfigureAwait(false);
        if (repoId is null || commitShaNullable is null) return (int)HarnessExitCode.IndexBuildFailure;
        var commitSha = commitShaNullable.Value;
        reporter.ReportIndexComplete(repo, TimeSpan.Zero, alreadyExisted);

        var suite = QuerySuiteFactory.Build(repo, repoId.Value);

        var results = new List<(IHarnessQuery Query, NormalizedResult Result)>();
        foreach (var query in suite.Queries)
        {
            var qr = await query.ExecuteAsync(engine, repo, commitSha, ct).ConfigureAwait(false);
            if (qr.Succeeded && qr.Result is not null)
                results.Add((query, qr.Result));
        }

        // --force guard: if >5 existing files would change, require --confirm
        var goldenDir = Path.Combine(goldenBaseDir, repo.Name);
        if (force && !confirm)
        {
            var changingCount = results.Count(r => File.Exists(HarnessIndexer.GoldenPath(goldenDir, r.Query)));
            if (changingCount > 5)
            {
                Console.Error.WriteLine(
                    $"ERROR: --force would overwrite {changingCount} golden files. " +
                    "Re-run with --force --confirm to proceed.");
                return (int)HarnessExitCode.ConfigurationError;
            }
        }

        // Without --force, refuse to overwrite existing files
        if (!force)
        {
            var existing = results.Where(r => File.Exists(HarnessIndexer.GoldenPath(goldenDir, r.Query))).ToList();
            if (existing.Count > 0)
            {
                Console.Error.WriteLine(
                    $"ERROR: {existing.Count} golden files already exist. " +
                    "Use --force to overwrite.");
                return (int)HarnessExitCode.ConfigurationError;
            }
        }

        Directory.CreateDirectory(goldenDir);
        foreach (var (query, result) in results)
        {
            var path = HarnessIndexer.GoldenPath(goldenDir, query);
            File.WriteAllText(path, JsonReporter.SerializeGolden(result));
        }

        Console.WriteLine($"[golden]  {repo.Name}: {results.Count} golden files written to {goldenDir}");
        return (int)HarnessExitCode.Success;
    }

    public async Task<int> CheckAsync(
        RepoDescriptor repo,
        IHarnessReporter reporter,
        CancellationToken ct)
    {
        reporter.ReportIndexStart(repo);
        var (repoId, commitShaNullable, alreadyExisted) = await indexer.IndexRepoAsync(repo, reporter, ct).ConfigureAwait(false);
        if (repoId is null || commitShaNullable is null) return (int)HarnessExitCode.IndexBuildFailure;
        var commitSha = commitShaNullable.Value;
        reporter.ReportIndexComplete(repo, TimeSpan.Zero, alreadyExisted);

        var goldenDir = Path.Combine(goldenBaseDir, repo.Name);
        if (!Directory.Exists(goldenDir))
        {
            Console.Error.WriteLine($"ERROR: No golden files found for {repo.Name} at {goldenDir}");
            Console.Error.WriteLine("Run: dotnet run -- golden save --repo micro");
            return (int)HarnessExitCode.ConfigurationError;
        }

        var suite = QuerySuiteFactory.Build(repo, repoId.Value);
        int passed = 0, failed = 0, missing = 0;

        foreach (var query in suite.Queries)
        {
            var goldenPath = HarnessIndexer.GoldenPath(goldenDir, query);
            if (!File.Exists(goldenPath))
            {
                missing++;
                continue;
            }

            var golden = JsonReporter.DeserializeGolden(File.ReadAllText(goldenPath));
            if (golden is null) { missing++; continue; }

            var qr = await query.ExecuteAsync(engine, repo, commitSha, ct).ConfigureAwait(false);
            var pairResult = QueryComparator.CompareWithGolden(query, qr, golden);
            reporter.ReportQueryResult(query, pairResult);

            if (pairResult.IsPass) passed++;
            else failed++;
        }

        reporter.ReportSummary(passed, failed, missing, TimeSpan.Zero);

        if (missing > 0)
            Console.WriteLine($"[golden]  {missing} queries had no golden file (run 'golden save' to add them)");

        return failed > 0 ? (int)HarnessExitCode.CorrectnessMismatch : (int)HarnessExitCode.Success;
    }
}
