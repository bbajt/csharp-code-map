namespace CodeMap.Integration.Tests.Concurrency;

using CodeMap.Core.Errors;
using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Runners;
using FluentAssertions;

/// <summary>
/// Tests for <see cref="ConcurrencyRunner"/>: pure guards (home validation, edit-target
/// selection) plus short end-to-end runs of real daemon processes against SampleSolution.
/// </summary>
public sealed class ConcurrencyRunnerTests : IDisposable
{
    private static readonly string[] LoopTools =
    [
        "symbols_search", "symbols_get_card", "refs_find", "graph_callers", "types_hierarchy",
        "graph_trace_feature", "symbols_get_context", "codemap_summarize", "index_refresh_overlay",
    ];

    private static readonly string Profile = OperatingSystem.IsWindows() ? @"C:\Users\bench" : "/home/bench";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "codemap-concurrency-test-" + Path.GetRandomFileName());

    /// <summary>Removes the run's data root.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_dataRoot);

    // ── Guards (unit) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".codemap")]
    [InlineData(".codemap/store")]
    public void ValidateHome_RealDataRootOrInside_Refuses(string relative)
    {
        var home = Path.Combine(Profile, relative.Replace('/', Path.DirectorySeparatorChar));

        var result = ConcurrencyRunner.ValidateHome(home, Profile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Theory]
    [InlineData(".codemap-bench")]
    [InlineData("tmp/codemap-harness/home")]
    public void ValidateHome_OtherDirectory_Succeeds(string relative)
    {
        var home = Path.Combine(Profile, relative.Replace('/', Path.DirectorySeparatorChar));

        var result = ConcurrencyRunner.ValidateHome(home, Profile);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void FindEditTarget_SampleSolution_PicksProductionSourceFile()
    {
        var solutionDir = Path.GetDirectoryName(KnownRepos.SampleSolution.SolutionPath)!;

        var target = ConcurrencyRunner.FindEditTarget(solutionDir);
        var rel = Path.GetRelativePath(solutionDir, target);

        rel.Should().EndWith(".cs");
        rel.Should().NotContain(".Tests");
        Path.GetFileName(rel).Should().NotBe("Program.cs");
        rel.Split(Path.DirectorySeparatorChar).Should().NotContain(["bin", "obj"]);
    }

    // ── End-to-end (real daemon processes) ────────────────────────────────────

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunOnceAsync_OneAgent_ShortRun_ZeroCorrectnessViolations()
    {
        var result = await NewRunner(WorkspaceMode.Isolated)
            .RunOnceAsync(KnownRepos.SampleSolution, agents: 1, TestContext.Current.CancellationToken);

        result.Passed.Should().BeTrue(string.Join("\n", result.SampleErrors));
        result.Correctness.OwnEditChecks.Should().BePositive();
        result.ErrorsByCode.Should().BeEmpty();
        result.Setup.RestoreMs.Should().BePositive("worktrees are restored before the daemons start");
        result.Tools.Select(t => t.Tool).Should().Contain(LoopTools);
        result.Setup.BaselineBuilds.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunOnceAsync_TwoAgentsIsolated_ProducesPerProcessMemoryAndStats()
    {
        var result = await NewRunner(WorkspaceMode.Isolated)
            .RunOnceAsync(KnownRepos.SampleSolution, agents: 2, TestContext.Current.CancellationToken);

        // Shape only: concurrent baseline builds may legitimately race (F6, PHASE-21-02),
        // so error counts and pass/fail are not asserted here.
        result.Agents.Should().Be(2);
        result.Memory.PerProcessPeakWorkingSetBytes.Should().HaveCount(2).And.OnlyContain(b => b > 0);
        result.Memory.SampleCount.Should().BePositive();
        result.Setup.InitializeMs.Should().HaveCount(2);
        result.Tools.Select(t => t.Tool).Should().Contain("index_ensure_baseline");
        if (result.Setup.AgentsFailedSetup == 0)
        {
            result.Tools.Select(t => t.Tool).Should().Contain(LoopTools);
            result.Correctness.CrossTalkProbes.Should().BePositive();
            result.Correctness.CrossTalkViolations.Should().Be(0);
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunOnceAsync_Completes_LeavesNoDaemonHomeBehind()
    {
        await NewRunner(WorkspaceMode.Isolated)
            .RunOnceAsync(KnownRepos.SampleSolution, agents: 1, TestContext.Current.CancellationToken);

        var runRoot = Directory.GetDirectories(Path.Combine(_dataRoot, "concurrency")).Should().ContainSingle().Subject;
        Directory.Exists(Path.Combine(runRoot, "home")).Should().BeFalse();
        Directory.Exists(Path.Combine(runRoot, "repo")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(runRoot, "logs"), "agent-*.stderr.log").Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunOnceAsync_TwoAgentsShared_SecondCreateReportsWorkspaceInUse()
    {
        var result = await NewRunner(WorkspaceMode.Shared)
            .RunOnceAsync(KnownRepos.SampleSolution, agents: 2, TestContext.Current.CancellationToken);

        // F3 itself is not fixed (Phase 2) — only labelled honestly and retryable (PHASE-21-02 T02).
        result.ErrorsByCode.Should().ContainKey("WORKSPACE_IN_USE", string.Join(" | ", result.SampleErrors));
        result.ErrorsByCode.Should().NotContainKeys("INVALID_ARGUMENT", "COMPILATION_FAILED");
        result.Passed.Should().BeFalse("one agent cannot set up while F3 exists");
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunOnceAsync_SharedCheckoutBlazor_OnlyF3SetupFailure_OwnerHasSymbols()
    {
        // F9 (PHASE-21-03 T01): both daemons evaluate the SAME checkout. Pre-fix, MSBuild raced
        // on obj/ and the workspace owner's symbol pool came back empty (2/2 observed runs).
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await NewRunner(WorkspaceMode.Shared)
                .RunOnceAsync(KnownRepos.SampleBlazorSolution, agents: 2, TestContext.Current.CancellationToken);

            var because = $"attempt {attempt}: {string.Join(" | ", result.SampleErrors)}";
            result.Setup.AgentsFailedSetup.Should().Be(1, because + " (only the F3 WORKSPACE_IN_USE agent)");
            result.ErrorsByCode.Should().ContainKey("WORKSPACE_IN_USE", because);
            result.Correctness.OwnEditChecks.Should().BePositive(because + " — the owner must be working");
            result.Correctness.OwnEditMissing.Should().Be(0, because);
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunAsync_TinyMemoryBudget_RunsFirstCount_SkipsLargerWithReason()
    {
        // PHASE-21-06 T01: the guard must be wired into the runner, not only unit-tested.
        // N=1 always runs (nothing measured yet); any real daemon exceeds 0.001 GB × 4.
        var options = NewOptions(WorkspaceMode.Isolated) with
        {
            AgentCounts = [4, 1],   // given out of order: the runner goes ascending
            Duration = TimeSpan.FromSeconds(3),
            MaxMemoryGb = 0.001,
        };

        var (report, exitCode) = await new ConcurrencyRunner(options, new ConcurrencyConsoleReporter())
            .RunAsync([KnownRepos.SampleSolution], TestContext.Current.CancellationToken);

        report.Runs.Select(r => r.Agents).Should().Equal(1);
        report.Skipped.Should().ContainSingle();
        var skip = report.Skipped![0];
        skip.RepoName.Should().Be("SampleSolution");
        skip.Agents.Should().Be(4);
        skip.BudgetGb.Should().Be(0.001);
        skip.ProjectedPeakGb.Should().BeGreaterThan(0.001);
        skip.Reason.Should().Contain("budget");
        exitCode.Should().Be(0, "a skip is not a failure");
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task RunAsync_OneRunThrows_IsRecordedAsFailed_AndLaterRunsStillExecute()
    {
        // Before: an exception in one run (here: git clone of a missing repo) escaped RunAsync,
        // crashed the harness and lost every run of the invocation (the JSON is written at the end).
        var missing = KnownRepos.SampleSolution with
        {
            Name = "MissingRepo",
            SolutionPath = Path.Combine(_dataRoot, "missing", "Missing.sln"),
            GitRoot = Path.Combine(_dataRoot, "missing"),
        };
        var options = NewOptions(WorkspaceMode.Isolated) with { Duration = TimeSpan.FromSeconds(3) };

        var (report, exitCode) = await new ConcurrencyRunner(options, new ConcurrencyConsoleReporter())
            .RunAsync([missing, KnownRepos.SampleSolution], TestContext.Current.CancellationToken);

        report.Failed.Should().ContainSingle();
        report.Failed![0].RepoName.Should().Be("MissingRepo");
        report.Failed[0].Agents.Should().Be(1);
        report.Failed[0].Error.Should().Contain("git clone");
        report.Runs.Should().ContainSingle().Which.Environment.RepoName.Should().Be("SampleSolution");
        report.Runs[0].Passed.Should().BeTrue(string.Join("\n", report.Runs[0].SampleErrors));
        exitCode.Should().Be((int)HarnessExitCode.IndexBuildFailure);
    }

    private ConcurrencyRunner NewRunner(WorkspaceMode mode) => new(NewOptions(mode), new ConcurrencyConsoleReporter());

    private ConcurrencyOptions NewOptions(WorkspaceMode mode) =>
        new ConcurrencyOptions(
            DataRoot: _dataRoot,
            DaemonPath: ConcurrencyOptions.DefaultDaemonPath(),
            AgentCounts: [1],
            Duration: TimeSpan.FromSeconds(6),
            Idle: TimeSpan.Zero,
            Seed: 42,
            WorkspaceMode: mode,
            CacheDir: null,
            KeepArtifacts: false,
            CallTimeout: TimeSpan.FromSeconds(120));
}
