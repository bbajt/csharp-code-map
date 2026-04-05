namespace CodeMap.Harness.Reports;

using CodeMap.Harness.Comparison;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Repos;

/// <summary>Writes harness results to stdout with ANSI-style symbols.</summary>
public sealed class ConsoleReporter : IHarnessReporter
{
    public void ReportIndexStart(RepoDescriptor repo) =>
        Console.WriteLine($"[index]   {repo.Name} ({repo.Tier}) ...");

    public void ReportIndexComplete(RepoDescriptor repo, TimeSpan elapsed, bool alreadyExisted)
    {
        var src = alreadyExisted ? "cached" : $"built in {elapsed.TotalSeconds:F1}s";
        Console.WriteLine($"[index]   {repo.Name} OK  ({src})");
    }

    public void ReportIndexFailed(RepoDescriptor repo, string message) =>
        Console.Error.WriteLine($"[index]   {repo.Name} FAILED: {message}");

    public void ReportAnchorCheck(RepoDescriptor repo, bool allPassed, int total)
    {
        var symbol = allPassed ? "✓" : "✗";
        Console.WriteLine($"[anchors] {repo.Name} {symbol}  {total} anchors");
    }

    public void ReportQueryResult(IHarnessQuery query, PairResult result)
    {
        switch (result)
        {
            case PairResult.Match m:
                Console.WriteLine($"  [PASS]  {query.Name}  ({m.Telemetry.LeftElapsed.TotalMilliseconds:F1}ms)");
                break;
            case PairResult.GoldenMatch m:
                Console.WriteLine($"  [PASS]  {query.Name}  ({m.Telemetry.LeftElapsed.TotalMilliseconds:F1}ms)");
                break;
            case PairResult.Mismatch mm:
                Console.WriteLine($"  [FAIL]  {query.Name}");
                foreach (var diff in mm.Differences.Take(5))
                    Console.WriteLine($"          {diff.FieldName}: expected={diff.LeftValue}  actual={diff.RightValue}");
                break;
            case PairResult.GoldenMismatch mm:
                Console.WriteLine($"  [FAIL]  {query.Name}");
                foreach (var diff in mm.Differences.Take(5))
                    Console.WriteLine($"          {diff.FieldName}: golden={diff.LeftValue}  actual={diff.RightValue}");
                break;
            case PairResult.LeftError le:
                Console.WriteLine($"  [ERR-L] {query.Name}  left engine error: {le.ErrorCode}");
                break;
            case PairResult.RightError re:
                Console.WriteLine($"  [ERR-R] {query.Name}  right engine error: {re.ErrorCode}");
                break;
            case PairResult.BothError be:
                Console.WriteLine($"  [ERR-B] {query.Name}  left={be.LeftCode}  right={be.RightCode}");
                break;
        }
    }

    public void ReportSummary(int passed, int failed, int skipped, TimeSpan totalElapsed)
    {
        Console.WriteLine();
        Console.WriteLine($"─────────────────────────────────────────");
        Console.WriteLine($"  Passed:  {passed}");
        Console.WriteLine($"  Failed:  {failed}");
        Console.WriteLine($"  Skipped: {skipped}");
        Console.WriteLine($"  Total:   {totalElapsed.TotalSeconds:F1}s");
        Console.WriteLine($"─────────────────────────────────────────");
    }
}
