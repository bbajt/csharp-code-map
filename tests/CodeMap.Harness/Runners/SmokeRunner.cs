namespace CodeMap.Harness.Runners;

using System.Diagnostics;
using CodeMap.Core.Interfaces;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;

/// <summary>
/// Fast CI smoke check: Micro + Small repos, IncludeInSmoke=true queries only.
/// Completes in under 60 seconds. Compares against committed golden files.
/// Exits 1 on any mismatch.
/// </summary>
public sealed class SmokeRunner(
    IQueryEngine engine,
    HarnessIndexer indexer,
    string goldenBaseDir)
{
    public async Task<int> RunAsync(IHarnessReporter reporter, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int totalPassed = 0, totalFailed = 0;

        foreach (var repo in KnownRepos.Committed)
        {
            reporter.ReportIndexStart(repo);
            var (repoId, commitShaNullable, alreadyExisted) = await indexer.IndexRepoAsync(repo, reporter, ct).ConfigureAwait(false);
            if (repoId is null || commitShaNullable is null) return (int)HarnessExitCode.IndexBuildFailure;
            var commitSha = commitShaNullable.Value;
            reporter.ReportIndexComplete(repo, TimeSpan.Zero, alreadyExisted);

            var goldenDir = Path.Combine(goldenBaseDir, repo.Name);
            if (!Directory.Exists(goldenDir))
            {
                Console.Error.WriteLine(
                    $"ERROR: No golden files for {repo.Name}. Run: dotnet run -- golden save --repo {repo.Tier.ToString().ToLowerInvariant()}");
                return (int)HarnessExitCode.ConfigurationError;
            }

            var suite = QuerySuiteFactory.Build(repo, repoId.Value);
            var smokeQueries = suite.SmokeQueries;

            foreach (var query in smokeQueries)
            {
                var goldenPath = HarnessIndexer.GoldenPath(goldenDir, query);
                if (!File.Exists(goldenPath)) continue;

                var golden = JsonReporter.DeserializeGolden(File.ReadAllText(goldenPath));
                if (golden is null) continue;

                var qr = await query.ExecuteAsync(engine, repo, commitSha, ct).ConfigureAwait(false);
                var pairResult = QueryComparator.CompareWithGolden(query, qr, golden);
                reporter.ReportQueryResult(query, pairResult);

                if (pairResult.IsPass) totalPassed++;
                else totalFailed++;
            }
        }

        reporter.ReportSummary(totalPassed, totalFailed, 0, sw.Elapsed);
        return totalFailed > 0 ? (int)HarnessExitCode.CorrectnessMismatch : (int)HarnessExitCode.Success;
    }
}
