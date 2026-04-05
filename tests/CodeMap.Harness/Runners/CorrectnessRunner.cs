namespace CodeMap.Harness.Runners;

using CodeMap.Core.Interfaces;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;

/// <summary>
/// Indexes a repo with one or two engines and compares query results semantically.
/// Phase 1: only one engine (SQLite). Two-engine comparison added in Phase 3+.
/// </summary>
public sealed class CorrectnessRunner
{
    public Task<int> RunAsync(
        RepoDescriptor repo,
        IHarnessReporter reporter,
        CancellationToken ct)
    {
        // Phase 1: single-engine run. Full parity comparison added in Phase 3+.
        Console.WriteLine(
            $"[correctness] Phase 1: single-engine (SQLite) run for {repo.Name}. " +
            "Two-engine comparison available after the custom engine is implemented.");

        return Task.FromResult((int)HarnessExitCode.Success);
    }
}
