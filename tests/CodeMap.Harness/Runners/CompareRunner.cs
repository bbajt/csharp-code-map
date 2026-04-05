namespace CodeMap.Harness.Runners;

using System.Diagnostics;
using CodeMap.Core.Interfaces;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;

/// <summary>
/// Engine comparison mode: indexes the same repo with both SQLite and v2 custom engines,
/// runs every query through both, and diffs the results side-by-side.
/// No golden files needed — the SQLite engine IS the reference.
/// </summary>
public sealed class CompareRunner(
    IQueryEngine sqliteEngine,
    HarnessIndexer sqliteIndexer,
    IQueryEngine customEngine,
    HarnessIndexer customIndexer)
{
    public async Task<int> RunAsync(
        IReadOnlyList<RepoDescriptor> repos,
        IHarnessReporter reporter,
        CancellationToken ct)
    {
        var totalPassed = 0;
        var totalFailed = 0;
        var totalErrors = 0;

        foreach (var repo in repos)
        {
            Console.WriteLine($"\n══════════════════════════════════════════════════════");
            Console.WriteLine($"  COMPARE: {repo.Name} ({repo.Tier})");
            Console.WriteLine($"══════════════════════════════════════════════════════\n");

            // ── Index with both engines ──────────────────────────────────────
            Console.Write($"  [sqlite]  Indexing {repo.Name}...");
            var swSqlite = Stopwatch.StartNew();
            var (sqliteRepoId, sqliteCommit, sqliteCached) =
                await sqliteIndexer.IndexRepoAsync(repo, reporter, ct).ConfigureAwait(false);
            swSqlite.Stop();

            if (sqliteRepoId is null || sqliteCommit is null)
            {
                Console.WriteLine($" FAILED");
                totalErrors++;
                continue;
            }
            Console.WriteLine($" OK ({(sqliteCached ? "cached" : $"{swSqlite.Elapsed.TotalSeconds:F1}s")})");

            Console.Write($"  [custom]  Indexing {repo.Name}...");
            var swCustom = Stopwatch.StartNew();
            var (customRepoId, customCommit, customCached) =
                await customIndexer.IndexRepoAsync(repo, reporter, ct).ConfigureAwait(false);
            swCustom.Stop();

            if (customRepoId is null || customCommit is null)
            {
                Console.WriteLine($" FAILED");
                totalErrors++;
                continue;
            }
            Console.WriteLine($" OK ({(customCached ? "cached" : $"{swCustom.Elapsed.TotalSeconds:F1}s")})");

            // Both must use the same repoId and commitSha
            if (sqliteRepoId.Value != customRepoId.Value || sqliteCommit.Value != customCommit.Value)
            {
                Console.Error.WriteLine($"  ERROR: repoId/commit mismatch — sqlite={sqliteRepoId.Value}/{sqliteCommit.Value.Value}, custom={customRepoId.Value}/{customCommit.Value.Value}");
                totalErrors++;
                continue;
            }

            // ── Run queries through both engines ─────────────────────────────
            var suite = QuerySuiteFactory.Build(repo, sqliteRepoId.Value);
            var commitSha = sqliteCommit.Value;
            int passed = 0, failed = 0, errors = 0;

            foreach (var query in suite.Queries)
            {
                ct.ThrowIfCancellationRequested();

                var sqliteResult = await query.ExecuteAsync(sqliteEngine, repo, commitSha, ct).ConfigureAwait(false);
                var customResult = await query.ExecuteAsync(customEngine, repo, commitSha, ct).ConfigureAwait(false);

                var pairResult = QueryComparator.Compare(query, sqliteResult, customResult);

                // Report
                switch (pairResult)
                {
                    case PairResult.Match m:
                        Console.WriteLine($"  [PASS]  {query.Name,-50} (sqlite={m.Telemetry.LeftElapsed.TotalMilliseconds:F1}ms  custom={m.Telemetry.RightElapsed.TotalMilliseconds:F1}ms)");
                        passed++;
                        break;
                    case PairResult.Mismatch mm:
                        Console.WriteLine($"  [FAIL]  {query.Name}");
                        foreach (var d in mm.Differences)
                            Console.WriteLine($"          {d.FieldName}: sqlite={d.LeftValue}  custom={d.RightValue}");
                        failed++;
                        break;
                    case PairResult.LeftError le:
                        Console.WriteLine($"  [ERR-L] {query.Name}  sqlite error: {le.ErrorCode}");
                        errors++;
                        break;
                    case PairResult.RightError re:
                        Console.WriteLine($"  [ERR-R] {query.Name}  custom error: {re.ErrorCode}");
                        errors++;
                        break;
                    case PairResult.BothError be when be.LeftCode == be.RightCode:
                        // Both engines error the same way — consistent behavior, count as pass
                        Console.WriteLine($"  [SKIP]  {query.Name,-50} (both: {be.LeftCode})");
                        passed++;
                        break;
                    case PairResult.BothError be:
                        Console.WriteLine($"  [ERR-B] {query.Name}  sqlite={be.LeftCode}  custom={be.RightCode}");
                        errors++;
                        break;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"  ─────────────────────────────────────────");
            Console.WriteLine($"  {repo.Name}: Passed={passed}  Failed={failed}  Errors={errors}  Total={suite.Queries.Count}");
            Console.WriteLine($"  ─────────────────────────────────────────");

            totalPassed += passed;
            totalFailed += failed;
            totalErrors += errors;
        }

        // ── Grand summary ────────────────────────────────────────────────────
        Console.WriteLine($"\n══════════════════════════════════════════════════════");
        Console.WriteLine($"  COMPARE SUMMARY: Passed={totalPassed}  Failed={totalFailed}  Errors={totalErrors}");
        Console.WriteLine($"══════════════════════════════════════════════════════\n");

        return (totalFailed + totalErrors) > 0
            ? (int)HarnessExitCode.CorrectnessMismatch
            : (int)HarnessExitCode.Success;
    }
}
