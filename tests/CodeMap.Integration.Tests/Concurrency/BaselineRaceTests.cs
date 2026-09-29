namespace CodeMap.Integration.Tests.Concurrency;

using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// End-to-end F6 regression (PHASE-21-02 T01): N real daemon processes indexing the same
/// (repo, commit) at the same moment must all complete setup without <c>ensure_baseline</c>
/// errors — concurrent publishers adopt the winner's baseline instead of deleting it.
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class BaselineRaceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "codemap-baseline-race-" + Path.GetRandomFileName());

    /// <summary>Removes the run data root.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_dataRoot);

    [Fact]
    public async Task FourAgentsIsolated_ConcurrentFirstIndex_NoEnsureBaselineErrors()
    {
        var runner = new ConcurrencyRunner(
            new ConcurrencyOptions(
                DataRoot: _dataRoot,
                DaemonPath: ConcurrencyOptions.DefaultDaemonPath(),
                AgentCounts: [4],
                Duration: TimeSpan.FromSeconds(3),
                Idle: TimeSpan.Zero,
                Seed: 42,
                WorkspaceMode: WorkspaceMode.Isolated,
                CacheDir: null,
                KeepArtifacts: false,
                CallTimeout: TimeSpan.FromSeconds(120)),
            new ConcurrencyConsoleReporter());

        // The pre-fix race was intermittent — repeat so a regression is very likely to show.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await runner.RunOnceAsync(KnownRepos.SampleSolution, agents: 4, TestContext.Current.CancellationToken);

            var because = $"attempt {attempt}: {string.Join(" | ", result.SampleErrors)}";
            result.Setup.AgentsFailedSetup.Should().Be(0, because);
            result.Tools.Single(t => t.Tool == "index.ensure_baseline").Errors.Should().Be(0, because);
            result.Setup.BaselineRequests.Should().Be(4, because + " (no retries needed)");
            result.Passed.Should().BeTrue(because);
        }
    }
}
