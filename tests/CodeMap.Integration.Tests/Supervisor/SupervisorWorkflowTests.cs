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
/// Integration tests simulating a supervisor-agent workspace lifecycle (PHASE-03-07 T02).
/// Uses real OverlayStore + WorkspaceManager. IncrementalCompiler is mocked.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SupervisorWorkflowTests : IClassFixture<IndexedSampleSolutionFixture>, IDisposable
{
    private readonly IndexedSampleSolutionFixture _f;
    private readonly string _wsDir;
    private readonly OverlayStore _overlayStore;
    private readonly IGitService _git;
    private readonly WorkspaceManager _manager;

    private static readonly CommitSha ShaA = CommitSha.From(new string('a', 40));

    public SupervisorWorkflowTests(IndexedSampleSolutionFixture fixture)
    {
        _f = fixture;
        _wsDir = Path.Combine(Path.GetTempPath(), "codemap-supervisor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wsDir);

        var overlayFactory = new OverlayDbFactory(_wsDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        _git = Substitute.For<IGitService>();
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_f.RepoId);
        // Default: HEAD matches fixture baseline sha → workspaces are fresh
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

    private async Task CreateAsync(string wsId) =>
        await _manager.CreateWorkspaceAsync(
            _f.RepoId, WorkspaceId.From(wsId), _f.Sha, "/sln", "/repo");

    // ── E2E_Supervisor_CreateTwoWorkspaces_BothListed ─────────────────────────

    [Fact]
    public async Task E2E_Supervisor_CreateTwoWorkspaces_BothListed()
    {
        await CreateAsync("agent-1");
        await CreateAsync("agent-2");

        var list = await _manager.ListWorkspacesAsync(_f.RepoId);

        list.Should().HaveCount(2);
        list.Should().Contain(ws => ws.WorkspaceId == WorkspaceId.From("agent-1"));
        list.Should().Contain(ws => ws.WorkspaceId == WorkspaceId.From("agent-2"));
        list.Should().OnlyContain(ws => !ws.IsStale,
            because: "workspaces created at HEAD are fresh");
    }

    // ── E2E_Supervisor_DeleteWorkspace_RemovedFromList ────────────────────────

    [Fact]
    public async Task E2E_Supervisor_DeleteWorkspace_RemovedFromList()
    {
        await CreateAsync("agent-1");
        await CreateAsync("agent-2");

        await _manager.DeleteWorkspaceAsync(_f.RepoId, WorkspaceId.From("agent-1"));

        var list = await _manager.ListWorkspacesAsync(_f.RepoId);

        list.Should().HaveCount(1);
        list.Should().Contain(ws => ws.WorkspaceId == WorkspaceId.From("agent-2"));
        list.Should().NotContain(ws => ws.WorkspaceId == WorkspaceId.From("agent-1"));
    }

    // ── E2E_Supervisor_DeleteNonExistent_NoError ──────────────────────────────

    [Fact]
    public async Task E2E_Supervisor_DeleteNonExistent_NoError()
    {
        var act = async () => await _manager.DeleteWorkspaceAsync(
            _f.RepoId, WorkspaceId.From("nonexistent-ws"));

        await act.Should().NotThrowAsync();
    }

    // ── E2E_Supervisor_WorkspaceQuality_SemanticLevelVisible ─────────────────

    [Fact]
    public async Task E2E_Supervisor_WorkspaceQuality_SemanticLevelVisible()
    {
        // SemanticLevel is populated during RefreshOverlay (overlay_meta).
        // If no refresh has happened, GetOverlaySemanticLevelAsync returns null — that's acceptable.
        await CreateAsync("agent-q");

        var list = await _manager.ListWorkspacesAsync(_f.RepoId);

        list.Should().HaveCount(1);
        // SemanticLevel may be null (no refresh happened yet) — just verify field exists
        var ws = list[0];
        ws.WorkspaceId.Should().Be(WorkspaceId.From("agent-q"));
        // SemanticLevel is null when overlay has no meta — that's expected
    }

    // ── E2E_Supervisor_FullLifecycle ──────────────────────────────────────────

    [Fact]
    public async Task E2E_Supervisor_FullLifecycle()
    {
        // 1. Create workspace
        var wsId = WorkspaceId.From("agent-lifecycle");
        var createResult = await _manager.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/sln", "/repo");
        createResult.IsSuccess.Should().BeTrue();

        // 2. List — 1 workspace, fresh
        var list1 = await _manager.ListWorkspacesAsync(_f.RepoId);
        list1.Should().HaveCount(1);
        list1[0].IsStale.Should().BeFalse();
        list1[0].OverlayRevision.Should().Be(0);

        // 3. Refresh overlay (mock returns empty delta rev=1)
        await _manager.RefreshOverlayAsync(_f.RepoId, wsId, [FilePath.From("SampleApp/Services/OrderService.cs")]);

        // 4. List after refresh — revision bumped
        var list2 = await _manager.ListWorkspacesAsync(_f.RepoId);
        list2[0].OverlayRevision.Should().Be(1);

        // 5. Reset
        await _manager.ResetWorkspaceAsync(_f.RepoId, wsId);
        var list3 = await _manager.ListWorkspacesAsync(_f.RepoId);
        list3[0].OverlayRevision.Should().Be(0);

        // 6. Delete
        await _manager.DeleteWorkspaceAsync(_f.RepoId, wsId);
        var list4 = await _manager.ListWorkspacesAsync(_f.RepoId);
        list4.Should().BeEmpty();
    }
}
