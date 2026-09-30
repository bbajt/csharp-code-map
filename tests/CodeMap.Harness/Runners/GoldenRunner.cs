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

    /// <summary>Runs <see cref="CheckWithOutcomeAsync"/> and returns its exit code.</summary>
    public async Task<int> CheckAsync(
        RepoDescriptor repo,
        IHarnessReporter reporter,
        CancellationToken ct) =>
        (await CheckWithOutcomeAsync(repo, reporter, ct).ConfigureAwait(false)).ExitCode;

    /// <summary>
    /// Indexes <paramref name="repo"/>, runs its query suite and compares each result with the golden
    /// file. Returns the per-query outcome (PHASE-21-12 T01).
    /// </summary>
    public async Task<GoldenCheckOutcome> CheckWithOutcomeAsync(
        RepoDescriptor repo,
        IHarnessReporter reporter,
        CancellationToken ct)
    {
        reporter.ReportIndexStart(repo);
        var (repoId, commitShaNullable, alreadyExisted) = await indexer.IndexRepoAsync(repo, reporter, ct).ConfigureAwait(false);
        if (repoId is null || commitShaNullable is null)
            return GoldenCheckOutcome.Error((int)HarnessExitCode.IndexBuildFailure);
        var commitSha = commitShaNullable.Value;
        reporter.ReportIndexComplete(repo, TimeSpan.Zero, alreadyExisted);

        var goldenDir = Path.Combine(goldenBaseDir, repo.Name);
        if (!Directory.Exists(goldenDir))
        {
            Console.Error.WriteLine($"ERROR: No golden files found for {repo.Name} at {goldenDir}");
            Console.Error.WriteLine("Run: dotnet run -- golden save --repo micro");
            return GoldenCheckOutcome.Error((int)HarnessExitCode.ConfigurationError);
        }

        var suite = QuerySuiteFactory.Build(repo, repoId.Value);
        var passed = new List<string>();
        var failed = new List<string>();
        var missing = new List<string>();
        var skipped = new List<string>();

        foreach (var query in suite.Queries)
        {
            var qr = await query.ExecuteAsync(engine, repo, commitSha, ct).ConfigureAwait(false);

            var goldenPath = HarnessIndexer.GoldenPath(goldenDir, query);
            var golden = File.Exists(goldenPath) ? JsonReporter.DeserializeGolden(File.ReadAllText(goldenPath)) : null;
            if (golden is null)
            {
                // Mirrors SaveAsync: a query that returns a result gets a golden file, so a missing one is a
                // failure (a renamed query, a deleted file). One without a result never had a golden file.
                if (qr.Succeeded && qr.Result is not null) missing.Add(query.Name);
                else skipped.Add(query.Name);
                continue;
            }

            var pairResult = QueryComparator.CompareWithGolden(query, qr, golden);
            reporter.ReportQueryResult(query, pairResult);

            if (pairResult.IsPass) passed.Add(query.Name);
            else failed.Add(query.Name);
        }

        reporter.ReportSummary(passed.Count, failed.Count + missing.Count, skipped.Count, TimeSpan.Zero);

        if (missing.Count > 0)
        {
            Console.WriteLine($"[golden]  FAIL: {missing.Count} queries returned a result but have no golden file:");
            foreach (var name in missing)
                Console.WriteLine($"[golden]    {name}  (expected {Path.GetFileName(HarnessIndexer.GoldenPath(goldenDir, suite.Queries.First(q => q.Name == name)))})");
        }
        if (skipped.Count > 0)
            Console.WriteLine($"[golden]  {skipped.Count} queries returned no result and have no golden file (skipped)");

        var exit = failed.Count + missing.Count > 0 ? (int)HarnessExitCode.CorrectnessMismatch : (int)HarnessExitCode.Success;
        return new GoldenCheckOutcome(exit, passed, failed, missing, skipped);
    }
}

/// <summary>
/// Result of one repo's golden check.
/// <paramref name="MissingGolden"/>: queries that returned a result but have no golden file (a failure).
/// <paramref name="Skipped"/>: queries with no golden file that returned no result either
/// (<c>golden save</c> never writes one for them).
/// </summary>
public sealed record GoldenCheckOutcome(
    int ExitCode,
    IReadOnlyList<string> Passed,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> MissingGolden,
    IReadOnlyList<string> Skipped)
{
    /// <summary>An outcome for a check that could not run (index or configuration failure).</summary>
    public static GoldenCheckOutcome Error(int exitCode) => new(exitCode, [], [], [], []);
}
