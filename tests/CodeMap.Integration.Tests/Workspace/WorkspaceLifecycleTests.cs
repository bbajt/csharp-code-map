namespace CodeMap.Integration.Tests.Workspace;

using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Integration tests for WorkspaceManager lifecycle (create, refresh, reset, delete).
/// Uses real BaselineStore + OverlayStore, but mocks IncrementalCompiler + GitService.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkspaceLifecycleTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "codemap-ws-lifecycle-" + Guid.NewGuid().ToString("N"));

    private static readonly RepoId Repo = RepoId.From("ws-lifecycle-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));
    private static readonly WorkspaceId WsA = WorkspaceId.From("ws-lifecycle-A");
    private static readonly WorkspaceId WsB = WorkspaceId.From("ws-lifecycle-B");

    private readonly ISymbolStore _baseline;
    private readonly IOverlayStore _overlay;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ICacheService _cache = new InMemoryCacheService();
    private readonly WorkspaceManager _manager;

    public WorkspaceLifecycleTests()
    {
        Directory.CreateDirectory(_tempDir);

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        var baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);
        // Create a minimal baseline so BaselineExistsAsync returns true
        baselineStore.CreateBaselineAsync(Repo, Sha, MakeEmptyCompilation(), repoRootPath: _tempDir).GetAwaiter().GetResult();

        _baseline = baselineStore;

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlay = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        // Default compiler returns empty delta
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(OverlayDelta.Empty(newRevision: 1));

        _git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FileChange>());

        _manager = new WorkspaceManager(
            _overlay, _compiler, _baseline, _git, _cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_Create_ThenStatus_ShowsWorkspace()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsA, Sha, "/fake/solution.sln", _tempDir);

        var workspaces = await _manager.ListWorkspacesAsync(Repo);

        workspaces.Should().HaveCount(1);
        workspaces[0].WorkspaceId.Should().Be(WsA);
    }

    [Fact]
    public async Task Lifecycle_Create_ThenRefresh_ThenStatus_ShowsRevision1()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsA, Sha, "/fake/solution.sln", _tempDir);

        // Explicit file path triggers compilation — mock returns revision 1
        await _manager.RefreshOverlayAsync(Repo, WsA, [FilePath.From("Fake/Fake.cs")]);

        var workspaces = await _manager.ListWorkspacesAsync(Repo);
        workspaces[0].OverlayRevision.Should().Be(1);
    }

    [Fact]
    public async Task Lifecycle_Create_ThenReset_ThenStatus_ShowsRevision0()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsA, Sha, "/fake/solution.sln", _tempDir);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(OverlayDelta.Empty(newRevision: 2));
        await _manager.RefreshOverlayAsync(Repo, WsA, [FilePath.From("Fake/Fake.cs")]);

        await _manager.ResetWorkspaceAsync(Repo, WsA);

        var workspaces = await _manager.ListWorkspacesAsync(Repo);
        workspaces[0].OverlayRevision.Should().Be(0);
    }

    [Fact]
    public async Task Lifecycle_Create_AlreadyExists_IsIdempotent()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsA, Sha, "/fake/solution.sln", _tempDir);
        var result = await _manager.CreateWorkspaceAsync(Repo, WsA, Sha, "/fake/solution.sln", _tempDir);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentRevision.Should().Be(0);
    }

    [Fact]
    public async Task Lifecycle_Create_NoBaseline_ReturnsError()
    {
        var nonExistentSha = CommitSha.From(new string('9', 40));
        var result = await _manager.CreateWorkspaceAsync(Repo, WsA, nonExistentSha, "/fake/solution.sln", _tempDir);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task Lifecycle_TwoWorkspaces_Isolated()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsA, Sha, "/fake/solution.sln", _tempDir);
        await _manager.CreateWorkspaceAsync(Repo, WsB, Sha, "/fake/solution.sln", _tempDir);

        // Refresh A only
        await _manager.RefreshOverlayAsync(Repo, WsA, [FilePath.From("Fake/Fake.cs")]);

        var workspaces = await _manager.ListWorkspacesAsync(Repo);
        var wsA = workspaces.First(w => w.WorkspaceId == WsA);
        var wsB = workspaces.First(w => w.WorkspaceId == WsB);

        wsA.OverlayRevision.Should().Be(1);
        wsB.OverlayRevision.Should().Be(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CompilationResult MakeEmptyCompilation() =>
        new CompilationResult(
            Symbols: [],
            References: [],
            Files: [],
            Stats: new IndexStats(
                SymbolCount: 0,
                ReferenceCount: 0,
                FileCount: 0,
                ElapsedSeconds: 0,
                Confidence: CodeMap.Core.Enums.Confidence.High));
}
