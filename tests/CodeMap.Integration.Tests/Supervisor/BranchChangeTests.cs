namespace CodeMap.Integration.Tests.Supervisor;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Integration.Tests.Workflows;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Integration tests for stale workspace detection when the repo HEAD changes (PHASE-03-07 T02).
/// Simulates branch changes by returning different commit SHAs from mocked IGitService.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BranchChangeTests : IClassFixture<IndexedSampleSolutionFixture>, IDisposable
{
    private readonly IndexedSampleSolutionFixture _f;
    private readonly string _wsDir;
    private readonly OverlayStore _overlayStore;
    private readonly IGitService _git;
    private readonly WorkspaceManager _manager;

    private static readonly CommitSha ShaA = CommitSha.From(new string('a', 40)); // "baseline"
    private static readonly CommitSha ShaB = CommitSha.From(new string('b', 40)); // "new HEAD"

    public BranchChangeTests(IndexedSampleSolutionFixture fixture)
    {
        _f = fixture;
        _wsDir = Path.Combine(Path.GetTempPath(), "codemap-branch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wsDir);

        var overlayFactory = new OverlayDbFactory(_wsDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        _git = Substitute.For<IGitService>();
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_f.RepoId);
        // Default HEAD = fixture sha (fresh)
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_f.Sha);
        _git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var compiler = Substitute.For<IIncrementalCompiler>();
        compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(OverlayDelta.Empty(1)));

        _manager = new WorkspaceManager(
            _overlayStore, compiler, _f.BaselineStore, _git,
            new InMemoryCacheService(),
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_wsDir))
            try { Directory.Delete(_wsDir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task CreateFreshAsync(string wsId, CommitSha sha)
    {
        // Temporarily set HEAD to sha so workspace is created against it
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(sha);
        await _manager.CreateWorkspaceAsync(_f.RepoId, WorkspaceId.From(wsId), sha, "/sln", "/repo");
    }

    // ── E2E_BranchChange_WorkspaceBecomesStale ────────────────────────────────

    [Fact]
    public async Task E2E_BranchChange_WorkspaceBecomesStale()
    {
        // 1. Create workspace at fixture sha (HEAD = fixture.Sha)
        await CreateFreshAsync("agent-1", _f.Sha);

        // 2. Simulate HEAD moving to ShaB
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ShaB);

        // 3. List workspaces
        var list = await _manager.ListWorkspacesAsync(_f.RepoId);

        list.Should().HaveCount(1);
        list[0].IsStale.Should().BeTrue(
            because: "workspace was created at fixture sha, HEAD has moved to ShaB");
        list[0].BaseCommitSha.Should().Be(_f.Sha);
    }

    // ── E2E_BranchChange_NewBaseline_StaleStillDetected ──────────────────────

    [Fact]
    public async Task E2E_BranchChange_NewBaseline_StaleStillDetected()
    {
        // 1. Create workspace at fixture sha
        await CreateFreshAsync("agent-1", _f.Sha);

        // 2. HEAD moves to ShaB — workspace becomes stale
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ShaB);

        // 3. Even after a new baseline would be indexed at ShaB, the workspace
        //    is still stale (it was created against fixture.Sha)
        var list = await _manager.ListWorkspacesAsync(_f.RepoId);

        list[0].IsStale.Should().BeTrue();
        list[0].BaseCommitSha.Should().Be(_f.Sha,
            because: "workspace BaseCommitSha is immutable after creation");
    }

    // ── E2E_BranchChange_ResetAndRebuild_WorkspaceFresh ──────────────────────

    [Fact]
    public async Task E2E_BranchChange_ResetAndRebuild_WorkspaceFresh()
    {
        // 1. Create workspace at fixture sha — it's fresh
        await CreateFreshAsync("agent-1", _f.Sha);

        // 2. Simulate HEAD moving (workspace becomes stale)
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ShaB);

        var stale = await _manager.ListWorkspacesAsync(_f.RepoId);
        stale[0].IsStale.Should().BeTrue();

        // 3. Delete the stale workspace and restore HEAD (supervisor re-baselines at fixture sha)
        await _manager.DeleteWorkspaceAsync(_f.RepoId, WorkspaceId.From("agent-1"));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_f.Sha);

        // 4. Create new workspace against current HEAD (fixture sha — baseline exists)
        await _manager.CreateWorkspaceAsync(
            _f.RepoId, WorkspaceId.From("agent-1"), _f.Sha, "/sln", "/repo");

        // 5. List — agent-1 should be fresh (base == HEAD)
        var list = await _manager.ListWorkspacesAsync(_f.RepoId);

        list.Should().HaveCount(1);
        list[0].IsStale.Should().BeFalse(
            because: "workspace recreated at current HEAD is fresh");
    }

    // ── E2E_RepoStatus_IncludesStaleFlag ─────────────────────────────────────

    [Fact]
    public async Task E2E_RepoStatus_IncludesStaleFlag()
    {
        // Create workspace at fixture sha
        await CreateFreshAsync("agent-1", _f.Sha);

        // HEAD moves
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ShaB);

        // repo.status calls ListWorkspacesAsync which returns enriched summaries
        var workspaces = await _manager.ListWorkspacesAsync(_f.RepoId);

        workspaces.Should().HaveCount(1);
        workspaces[0].IsStale.Should().BeTrue(
            because: "repo.status Workspaces are enriched by ListWorkspacesAsync");
    }

    // ── GetStaleWorkspacesAsync ───────────────────────────────────────────────

    [Fact]
    public async Task E2E_GetStaleWorkspacesAsync_ReturnsOnlyStale()
    {
        // Create agent-1 at fixture sha, agent-2 at fixture sha
        await CreateFreshAsync("agent-1", _f.Sha);
        await CreateFreshAsync("agent-2", _f.Sha);

        // HEAD moves → both stale
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ShaB);

        var stale = await _manager.GetStaleWorkspacesAsync(_f.RepoId);

        stale.Should().HaveCount(2);
        stale.Should().OnlyContain(ws => ws.IsStale);
    }

    [Fact]
    public async Task E2E_GetStaleWorkspacesAsync_EmptyWhenAllFresh()
    {
        await CreateFreshAsync("agent-1", _f.Sha);
        // HEAD stays at fixture sha (no change)

        var stale = await _manager.GetStaleWorkspacesAsync(_f.RepoId);

        stale.Should().BeEmpty();
    }
}
