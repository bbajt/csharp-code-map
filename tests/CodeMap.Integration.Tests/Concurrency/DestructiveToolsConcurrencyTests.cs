namespace CodeMap.Integration.Tests.Concurrency;

using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// PHASE-21-10: the destructive tools with two real daemons on one <c>CODEMAP_HOME</c> and one RepoId
/// (two worktrees of one clone), the shape of a multi-agent session. Agent B holds a workspace; agent A
/// runs the destructive tool.
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class DestructiveToolsConcurrencyTests : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(180);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-destructive-" + Path.GetRandomFileName());

    /// <summary>Removes clones, data root and logs.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_root);

    [Fact]
    public async Task RemoveRepo_WhileOtherDaemonHasWorkspace_Refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = KnownRepos.SampleSolution;
        var solutionRel = Path.GetRelativePath(repo.GitRepoRoot, repo.SolutionPath);
        var home = Path.Combine(_root, "home");

        await using var clone = await AgentScratchRepo.CreateAsync(repo.GitRepoRoot, Path.Combine(_root, "clone"), 2, ct);
        var wtA = clone.Worktrees[0];
        var wtB = clone.Worktrees[1];

        await using var daemonA = await StartDaemonAsync(home, "a", ct);
        await using var daemonB = await StartDaemonAsync(home, "b", ct);

        await EnsureBaselineAsync(daemonA, wtA, solutionRel, ct);
        await EnsureBaselineAsync(daemonB, wtB, solutionRel, ct);
        var create = await daemonB.CallToolAsync("workspace_create",
            new JsonObject { ["repo_path"] = wtB, ["solution_path"] = Path.Combine(wtB, solutionRel), ["workspace_id"] = "agent-b" }, ct);
        create.Ok.Should().BeTrue($"{create.ErrorCode}: {create.ErrorMessage}");

        var remove = await daemonA.CallToolAsync("index_remove_repo",
            new JsonObject { ["repo_path"] = wtA, ["dry_run"] = false }, ct);

        remove.Ok.Should().BeFalse("agent B's workspace is live in another process");
        remove.ErrorCode.Should().Be("WORKSPACE_IN_USE");
        remove.ErrorMessage.Should().Contain("agent-b");

        var search = await daemonB.CallToolAsync("symbols_search",
            new JsonObject { ["repo_path"] = wtB, ["workspace_id"] = "agent-b", ["query"] = "OrderService" }, ct);
        search.Ok.Should().BeTrue($"B keeps working: {search.ErrorCode}: {search.ErrorMessage}");

        var repoDirs = Directory.GetDirectories(Path.Combine(home, "store"));
        repoDirs.Should().ContainSingle("both worktrees share one RepoId");
        Directory.GetDirectories(Path.Combine(repoDirs[0], "baselines")).Should().NotBeEmpty();
        Directory.Exists(Path.Combine(repoDirs[0], "overlays", "agent-b")).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_OtherDaemonsWorkspaceBaseline_Survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = KnownRepos.SampleSolution;
        var solutionRel = Path.GetRelativePath(repo.GitRepoRoot, repo.SolutionPath);
        var home = Path.Combine(_root, "home");

        await using var clone = await AgentScratchRepo.CreateAsync(repo.GitRepoRoot, Path.Combine(_root, "clone"), 2, ct);
        var wtA = clone.Worktrees[0];
        var wtB = clone.Worktrees[1];
        // B works on its own commit: neither A's HEAD nor one of A's workspaces.
        Git(wtB, "-c", "user.email=agent-b@test", "-c", "user.name=agent-b", "commit", "--allow-empty", "--quiet", "-m", "agent B");
        var shaB = Git(wtB, "rev-parse", "HEAD").Trim();

        await using var daemonA = await StartDaemonAsync(home, "a", ct);
        await using var daemonB = await StartDaemonAsync(home, "b", ct);

        await EnsureBaselineAsync(daemonB, wtB, solutionRel, ct);
        var create = await daemonB.CallToolAsync("workspace_create",
            new JsonObject { ["repo_path"] = wtB, ["solution_path"] = Path.Combine(wtB, solutionRel), ["workspace_id"] = "agent-b" }, ct);
        create.Ok.Should().BeTrue($"{create.ErrorCode}: {create.ErrorMessage}");
        await EnsureBaselineAsync(daemonA, wtA, solutionRel, ct);

        var cleanup = await daemonA.CallToolAsync("index_cleanup",
            new JsonObject { ["repo_path"] = wtA, ["keep_count"] = 0, ["dry_run"] = false }, ct);
        cleanup.Ok.Should().BeTrue($"{cleanup.ErrorCode}: {cleanup.ErrorMessage}");

        // Complete, not just present: on Windows the old recursive delete removed every segment B didn't
        // have mapped and left the directory behind (B's open segments kept its queries working).
        var repoDir = Directory.GetDirectories(Path.Combine(home, "store")).Single();
        var baselineB = Path.Combine(repoDir, "baselines", shaB);
        BaselineFiles.Where(f => !File.Exists(Path.Combine(baselineB, f))).Should().BeEmpty(
            "agent B's workspace is based on it, although B runs in another process");
        cleanup.Payload.Should().Contain("protected_by_workspaces").And.Contain(shaB);

        var search = await daemonB.CallToolAsync("symbols_search",
            new JsonObject { ["repo_path"] = wtB, ["workspace_id"] = "agent-b", ["query"] = "OrderService" }, ct);
        search.Ok.Should().BeTrue($"B keeps working: {search.ErrorCode}: {search.ErrorMessage}");
    }

    /// <summary>Every file of a complete baseline (mirrors <c>BaselinePublisher.RequiredFiles</c>).</summary>
    private static readonly string[] BaselineFiles =
    [
        "manifest.json", "checksums.bin", "dictionary.seg", "search.idx", "content.seg", "symbols.seg",
        "files.seg", "projects.seg", "edges.seg", "facts.seg", "adjacency-out.idx", "adjacency-in.idx",
    ];

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", dir, .. args])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var git = Process.Start(psi)!;
        var stdout = git.StandardOutput.ReadToEnd();
        var stderr = git.StandardError.ReadToEnd();
        git.WaitForExit();
        git.ExitCode.Should().Be(0, stderr);
        return stdout;
    }

    private static async Task EnsureBaselineAsync(IMcpClient daemon, string worktree, string solutionRel, CancellationToken ct)
    {
        var baseline = await daemon.CallToolAsync("index_ensure_baseline",
            new JsonObject { ["repo_path"] = worktree, ["solution_path"] = Path.Combine(worktree, solutionRel) }, ct);
        baseline.Ok.Should().BeTrue($"{baseline.ErrorCode}: {baseline.ErrorMessage}");
    }

    private Task<StdioMcpClient> StartDaemonAsync(string home, string name, CancellationToken ct)
        => StdioMcpClient.StartAsync(
            ConcurrencyOptions.DefaultDaemonPath(),
            new Dictionary<string, string?> { ["CODEMAP_HOME"] = home, ["CODEMAP_CACHE_DIR"] = null },
            Path.Combine(_root, $"daemon-{name}.stderr.log"),
            CallTimeout,
            ct);
}
