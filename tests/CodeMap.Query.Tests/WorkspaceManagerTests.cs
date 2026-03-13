namespace CodeMap.Query.Tests;

using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class WorkspaceManagerTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-001");
    private static readonly string SlnPath = "/fake/solution.sln";
    private static readonly string RepoRoot = "/fake/repo";

    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();
    private readonly ISymbolStore _baseline = Substitute.For<ISymbolStore>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly WorkspaceManager _manager;

    public WorkspaceManagerTests()
    {
        _manager = new WorkspaceManager(
            _overlay, _compiler, _baseline, _git, _cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
    }

    // ── CreateWorkspaceAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidInputs_CreatesOverlayAndRegisters()
    {
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);

        result.IsSuccess.Should().BeTrue();
        await _overlay.Received(1).CreateOverlayAsync(Repo, WsId, Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_BaselineNotExists_ReturnsIndexNotAvailable()
    {
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task Create_AlreadyExists_ReturnsCurrentState_Idempotent()
    {
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);

        // Second call should be idempotent
        var result = await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);

        result.IsSuccess.Should().BeTrue();
        // CreateOverlayAsync should only be called once
        await _overlay.Received(1).CreateOverlayAsync(Repo, WsId, Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_SetsRevisionToZero()
    {
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);

        result.Value.CurrentRevision.Should().Be(0);
    }

    [Fact]
    public async Task Create_StoresBaselineCommitSha()
    {
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);

        result.Value.BaselineCommitSha.Should().Be(Sha);
    }

    // ── RefreshOverlayAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_ExplicitFiles_PassesToIncrementalCompiler()
    {
        await SetupWorkspaceAsync();
        var files = new[] { FilePath.From("src/Foo.cs") };
        var delta = MakeDelta(newRevision: 1);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(delta));

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, files);

        result.IsSuccess.Should().BeTrue();
        await _compiler.Received(1).ComputeDeltaAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<FilePath>>(fp => fp.Count == 1 && fp[0] == FilePath.From("src/Foo.cs")),
            Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_NoExplicitFiles_AutoDetectsViaGitDiff()
    {
        await SetupWorkspaceAsync();
        var changed = new FilePath[] { FilePath.From("src/Foo.cs") };
        _git.GetChangedFilesAsync(RepoRoot, Sha, Arg.Any<CancellationToken>())
            .Returns(changed.Select(f => new FileChange(f, FileChangeKind.Modified)).ToList());
        var delta = MakeDelta(newRevision: 1);
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(delta));

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, null);

        result.IsSuccess.Should().BeTrue();
        await _git.Received(1).GetChangedFilesAsync(RepoRoot, Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_NoChanges_ReturnsZeroReindexed()
    {
        await SetupWorkspaceAsync();
        _git.GetChangedFilesAsync(RepoRoot, Sha, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FileChange>());

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.FilesReindexed.Should().Be(0);
        result.Value.SymbolsUpdated.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_WithChangedFiles_AppliesDeltaToOverlay()
    {
        await SetupWorkspaceAsync();
        var files = new[] { FilePath.From("src/Foo.cs") };
        var delta = MakeDelta(newRevision: 1);
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(delta));

        await _manager.RefreshOverlayAsync(Repo, WsId, files);

        await _overlay.Received(1).ApplyDeltaAsync(Repo, WsId, delta, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithDeletedFiles_MarksBaselineSymbolsDeleted()
    {
        await SetupWorkspaceAsync();
        var deletedFile = FilePath.From("src/Old.cs");
        _git.GetChangedFilesAsync(RepoRoot, Sha, Arg.Any<CancellationToken>())
            .Returns(new[] { new FileChange(deletedFile, FileChangeKind.Deleted) }.ToList());
        _baseline.GetSymbolsByFileAsync(Repo, Sha, deletedFile, Arg.Any<CancellationToken>())
                 .Returns(new[] { MakeSymbolCard("T:OldClass") }.ToList());
        _overlay.GetRevisionAsync(Repo, WsId, Arg.Any<CancellationToken>()).Returns(0);

        await _manager.RefreshOverlayAsync(Repo, WsId, null);

        await _overlay.Received(1).ApplyDeltaAsync(
            Repo, WsId,
            Arg.Is<OverlayDelta>(d => d.DeletedSymbolIds.Any(id => id.Value == "T:OldClass")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_IncrementsRevision()
    {
        await SetupWorkspaceAsync();
        var files = new[] { FilePath.From("src/Foo.cs") };
        var delta = MakeDelta(newRevision: 1);
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(delta));

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, files);

        result.Value.NewOverlayRevision.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_InvalidatesWorkspaceCache()
    {
        await SetupWorkspaceAsync();
        var files = new[] { FilePath.From("src/Foo.cs") };
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(MakeDelta(newRevision: 1)));

        await _manager.RefreshOverlayAsync(Repo, WsId, files);

        await _cache.Received(1).InvalidateAsync(
            Arg.Is<string>(k => k.Contains(WsId.Value)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_UpdatesRegistryRevision()
    {
        await SetupWorkspaceAsync();
        var files = new[] { FilePath.From("src/Foo.cs") };
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(MakeDelta(newRevision: 3)));

        await _manager.RefreshOverlayAsync(Repo, WsId, files);

        _manager.GetWorkspaceInfo(Repo, WsId)!.CurrentRevision.Should().Be(3);
    }

    [Fact]
    public async Task Refresh_WorkspaceNotFound_ReturnsNotFound()
    {
        var result = await _manager.RefreshOverlayAsync(Repo, WsId, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Refresh_CompilationFails_ReturnsError()
    {
        await SetupWorkspaceAsync();
        var files = new[] { FilePath.From("src/Foo.cs") };
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromException<OverlayDelta>(new InvalidOperationException("Build failed")));

        var act = async () => await _manager.RefreshOverlayAsync(Repo, WsId, files);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── ResetWorkspaceAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task Reset_ClearsOverlayData()
    {
        await SetupWorkspaceAsync();

        await _manager.ResetWorkspaceAsync(Repo, WsId);

        await _overlay.Received(1).ResetOverlayAsync(Repo, WsId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_InvalidatesCache()
    {
        await SetupWorkspaceAsync();

        await _manager.ResetWorkspaceAsync(Repo, WsId);

        await _cache.Received(1).InvalidateAsync(
            Arg.Is<string>(k => k.Contains(WsId.Value)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_ResetsRevisionToZero()
    {
        await SetupWorkspaceAsync();

        var result = await _manager.ResetWorkspaceAsync(Repo, WsId);

        result.Value.NewRevision.Should().Be(0);
        _manager.GetWorkspaceInfo(Repo, WsId)!.CurrentRevision.Should().Be(0);
    }

    [Fact]
    public async Task Reset_ReturnsPreviousRevision()
    {
        await SetupWorkspaceAsync();
        // Simulate revision being 3
        var files = new[] { FilePath.From("src/Foo.cs") };
        _compiler.ComputeDeltaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(MakeDelta(newRevision: 3)));
        await _manager.RefreshOverlayAsync(Repo, WsId, files);

        var result = await _manager.ResetWorkspaceAsync(Repo, WsId);

        result.Value.PreviousRevision.Should().Be(3);
    }

    [Fact]
    public async Task Reset_WorkspaceNotFound_ReturnsNotFound()
    {
        var result = await _manager.ResetWorkspaceAsync(Repo, WsId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ── DeleteWorkspaceAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesFromRegistry()
    {
        await SetupWorkspaceAsync();

        await _manager.DeleteWorkspaceAsync(Repo, WsId);

        _manager.GetWorkspaceInfo(Repo, WsId).Should().BeNull();
    }

    [Fact]
    public async Task Delete_DeletesOverlayDb()
    {
        await SetupWorkspaceAsync();

        await _manager.DeleteWorkspaceAsync(Repo, WsId);

        await _overlay.Received(1).DeleteOverlayAsync(Repo, WsId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotFound_NoOp()
    {
        // Should not throw if workspace not in registry
        await _manager.DeleteWorkspaceAsync(Repo, WsId);

        await _overlay.Received(1).DeleteOverlayAsync(Repo, WsId, Arg.Any<CancellationToken>());
    }

    // ── ListWorkspacesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsAllWorkspacesForRepo()
    {
        await SetupWorkspaceAsync();
        var ws2 = WorkspaceId.From("ws-002");
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
        await _manager.CreateWorkspaceAsync(Repo, ws2, Sha, SlnPath, RepoRoot);
        _overlay.GetOverlayFilePathsAsync(Repo, WsId, Arg.Any<CancellationToken>()).Returns(new HashSet<FilePath>());
        _overlay.GetOverlayFilePathsAsync(Repo, ws2, Arg.Any<CancellationToken>()).Returns(new HashSet<FilePath>());

        var list = await _manager.ListWorkspacesAsync(Repo);

        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_EmptyWhenNoWorkspaces()
    {
        var list = await _manager.ListWorkspacesAsync(Repo);

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task List_IncludesRevisionAndFileCount()
    {
        await SetupWorkspaceAsync();
        _overlay.GetOverlayFilePathsAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(new HashSet<FilePath> { FilePath.From("src/Foo.cs"), FilePath.From("src/Bar.cs") });

        var list = await _manager.ListWorkspacesAsync(Repo);

        list[0].OverlayRevision.Should().Be(0);
        list[0].ModifiedFileCount.Should().Be(2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SetupWorkspaceAsync()
    {
        _baseline.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
        _overlay.GetRevisionAsync(Repo, WsId, Arg.Any<CancellationToken>()).Returns(0);
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, RepoRoot);
    }

    private static OverlayDelta MakeDelta(int newRevision = 1) =>
        OverlayDelta.Empty(newRevision);

    private static SymbolCard MakeSymbolCard(string symbolId) =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(symbolId),
            fullyQualifiedName: symbolId,
            kind: CodeMap.Core.Enums.SymbolKind.Class,
            signature: symbolId,
            @namespace: "OldNs",
            filePath: FilePath.From("src/Old.cs"),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: CodeMap.Core.Enums.Confidence.High);
}
