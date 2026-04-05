namespace CodeMap.Harness.Reports;

using CodeMap.Harness.Comparison;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Repos;

/// <summary>Outputs harness run results in a specific format.</summary>
public interface IHarnessReporter
{
    void ReportIndexStart(RepoDescriptor repo);
    void ReportIndexComplete(RepoDescriptor repo, TimeSpan elapsed, bool alreadyExisted);
    void ReportIndexFailed(RepoDescriptor repo, string message);
    void ReportAnchorCheck(RepoDescriptor repo, bool allPassed, int total);
    void ReportQueryResult(IHarnessQuery query, PairResult result);
    void ReportSummary(int passed, int failed, int skipped, TimeSpan totalElapsed);
}
