namespace CodeMap.Integration.Tests.Concurrency;

using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// Tests for <see cref="AgentScratchRepo"/>: real git clones/worktrees and <c>dotnet restore</c>
/// (no daemon).
/// </summary>
public sealed class AgentScratchRepoTests : IDisposable
{
    private static readonly string SolutionRel = Path.GetRelativePath(
        KnownRepos.SampleSolution.GitRepoRoot, KnownRepos.SampleSolution.SolutionPath);

    private static readonly string TestsAssetsRel = Path.Combine(
        Path.GetDirectoryName(SolutionRel)!, "SampleApp.Tests", "obj", "project.assets.json");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-scratch-" + Path.GetRandomFileName());

    /// <summary>Removes the scratch clone.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_root);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_WorktreesAreUnrestored()
    {
        // PHASE-21-06 T02 finding: obj/ is gitignored, so a fresh worktree has no
        // project.assets.json, and MSBuildWorkspace doesn't restore. The eShopOnWeb probe
        // indexed with thousands of compile errors per project (e.g. "Project Web has 6996
        // compilation error(s)"). This pins the precondition RestoreAsync exists for.
        await using var scratch = await AgentScratchRepo.CreateAsync(
            KnownRepos.SampleSolution.GitRepoRoot, _root, worktreeCount: 1, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(scratch.Worktrees[0], TestsAssetsRel)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestoreAsync_EveryWorktreeGetsProjectAssets()
    {
        await using var scratch = await AgentScratchRepo.CreateAsync(
            KnownRepos.SampleSolution.GitRepoRoot, _root, worktreeCount: 2, TestContext.Current.CancellationToken);

        var elapsed = await scratch.RestoreAsync(SolutionRel, TestContext.Current.CancellationToken);

        elapsed.Should().BePositive();
        foreach (var wt in scratch.Worktrees)
            File.Exists(Path.Combine(wt, TestsAssetsRel)).Should().BeTrue($"{wt} must be restored");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_RepoWithPathsBeyond260Chars_ClonesAndChecksOut()
    {
        // PHASE-21-06 T02: every Bitwarden run failed in `git worktree add` with "Filename too long"
        // (e.g. util/Migrator/DbScripts/2025-12-05_00_UpdateOrganization…IntegrationType.sql under a
        // ~110-char scratch prefix). Windows had LongPathsEnabled=1, but git's core.longpaths was unset.
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "src");
        // 255 chars on its own, so the checked-out path exceeds 260 under any temp prefix (Linux's
        // /tmp is much shorter than Windows' %TEMP%; with 50-char segments it came out at 257 there).
        var relative = Path.Combine(
            new string('a', 60), new string('b', 60), new string('c', 60), new string('d', 60), "LongPath.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
        await File.WriteAllTextAsync(Path.Combine(source, relative), "class LongPath { }\n", ct);
        await GitAsync(source, ct, "init", "--quiet");
        await GitAsync(source, ct, "-c", "core.longpaths=true", "add", "--all");
        await GitAsync(source, ct, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "core.longpaths=true",
            "commit", "--quiet", "-m", "long path");

        await using var scratch = await AgentScratchRepo.CreateAsync(source, Path.Combine(_root, "scratch"), 1, ct);

        var checkedOut = Path.Combine(scratch.Worktrees[0], relative);
        checkedOut.Length.Should().BeGreaterThan(260, "the test must actually exceed the classic Windows limit");
        File.Exists(checkedOut).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestoreAsync_Failure_ThrowsWithTheRestoreOutput()
    {
        await using var scratch = await AgentScratchRepo.CreateAsync(
            KnownRepos.SampleSolution.GitRepoRoot, _root, worktreeCount: 1, TestContext.Current.CancellationToken);

        var act = () => scratch.RestoreAsync("does-not-exist.sln", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("dotnet restore").And.Contain("does-not-exist.sln");
    }

    private static async Task GitAsync(string workingDir, CancellationToken ct, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', args)}: {await stderr}");
    }
}
