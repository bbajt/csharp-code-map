namespace CodeMap.Integration.Tests.Concurrency;

using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// End-to-end regression for repo-scoped overlays (PHASE-21-03 T02, ADR-043): two daemons on
/// one <c>CODEMAP_HOME</c>, serving two <em>different</em> repos, both create the workspace
/// <c>"session"</c> (what CLAUDE.MD tells every agent to use). Pre-fix both mapped to
/// <c>store/overlays/session/</c>, so the second got <c>WORKSPACE_IN_USE</c> although the repos
/// are unrelated. F3 remains for the same repo (see <c>ConcurrencyRunnerTests</c>).
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class WorkspaceRepoScopingTests : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(180);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-ws-scope-" + Path.GetRandomFileName());

    /// <summary>Removes clones, data root and logs.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_root);

    [Fact]
    public async Task TwoRepos_SameWorkspaceId_TwoDaemons_NoWorkspaceInUse()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = KnownRepos.SampleSolution;
        var solutionRel = Path.GetRelativePath(repo.GitRepoRoot, repo.SolutionPath);
        var home = Path.Combine(_root, "home");

        // Two clones without a remote → each derives its own local-<pathhash> RepoId.
        await using var cloneA = await AgentScratchRepo.CreateAsync(repo.GitRepoRoot, Path.Combine(_root, "a"), 1, ct);
        await using var cloneB = await AgentScratchRepo.CreateAsync(repo.GitRepoRoot, Path.Combine(_root, "b"), 1, ct);
        RemoveOrigin(Path.Combine(_root, "a", "source"));
        RemoveOrigin(Path.Combine(_root, "b", "source"));

        await using var daemonA = await StartDaemonAsync(home, "a", ct);
        await using var daemonB = await StartDaemonAsync(home, "b", ct);

        // A creates and holds "session" first; B then creates its own "session".
        await SetUpSessionAsync(daemonA, cloneA.Worktrees[0], solutionRel, ct);
        await SetUpSessionAsync(daemonB, cloneB.Worktrees[0], solutionRel, ct);

        // Both overlays live under their own repo's store directory.
        var sessions = Directory.GetDirectories(Path.Combine(home, "store"))
            .Where(d => Directory.Exists(Path.Combine(d, "overlays", "session")))
            .ToList();
        sessions.Should().HaveCount(2, "each repo has its own \"session\" overlay");
        Directory.Exists(Path.Combine(home, "store", "overlays")).Should().BeFalse();
    }

    private static async Task SetUpSessionAsync(IMcpClient daemon, string worktree, string solutionRel, CancellationToken ct)
    {
        var solution = Path.Combine(worktree, solutionRel);

        var baseline = await daemon.CallToolAsync("index_ensure_baseline",
            new JsonObject { ["repo_path"] = worktree, ["solution_path"] = solution }, ct);
        baseline.Ok.Should().BeTrue($"{baseline.ErrorCode}: {baseline.ErrorMessage}");

        var create = await daemon.CallToolAsync("workspace_create",
            new JsonObject { ["repo_path"] = worktree, ["solution_path"] = solution, ["workspace_id"] = "session" }, ct);
        create.Ok.Should().BeTrue($"{create.ErrorCode}: {create.ErrorMessage}");
    }

    private Task<StdioMcpClient> StartDaemonAsync(string home, string name, CancellationToken ct)
        => StdioMcpClient.StartAsync(
            ConcurrencyOptions.DefaultDaemonPath(),
            new Dictionary<string, string?> { ["CODEMAP_HOME"] = home, ["CODEMAP_CACHE_DIR"] = null },
            Path.Combine(_root, $"daemon-{name}.stderr.log"),
            CallTimeout,
            ct);

    private static void RemoveOrigin(string cloneDir)
    {
        var psi = new ProcessStartInfo("git", ["-C", cloneDir, "remote", "remove", "origin"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var git = Process.Start(psi)!;
        git.WaitForExit();
        git.ExitCode.Should().Be(0, git.StandardError.ReadToEnd());
    }
}
