namespace CodeMap.Integration.Tests.Workspace;

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
/// Integration tests for WorkspaceManager.RefreshOverlayAsync.
/// Uses a mock IncrementalCompiler (real compilation tested in IncrementalCompilerTests).
/// Uses real OverlayStore + BaselineStore.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OverlayRefreshTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "codemap-overlay-refresh-" + Guid.NewGuid().ToString("N"));

    private static readonly RepoId Repo = RepoId.From("overlay-refresh-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-refresh");
    private static readonly string SlnPath = "/fake/solution.sln";

    private readonly ISymbolStore _baseline;
    private readonly IOverlayStore _overlay;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ICacheService _cache = new InMemoryCacheService();
    private readonly WorkspaceManager _manager;

    public OverlayRefreshTests()
    {
        Directory.CreateDirectory(_tempDir);

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        var baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);
        baselineStore.CreateBaselineAsync(Repo, Sha, MakeEmptyCompilation(), repoRootPath: _tempDir).GetAwaiter().GetResult();
        _baseline = baselineStore;

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlay = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

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

    // ── Test cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_ModifyMethod_OverlayContainsUpdatedSymbol()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var changedFile = FilePath.From("src/Foo.cs");
        var updatedSymbol = MakeSymbol("T:Foo", changedFile);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile(FileId: "abc123", Path: changedFile, Sha256Hash: "deadbeef", ProjectName: null)],
                     AddedOrUpdatedSymbols: [updatedSymbol],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1));

        await _manager.RefreshOverlayAsync(Repo, WsId, [changedFile]);

        var card = await _overlay.GetOverlaySymbolAsync(Repo, WsId, SymbolId.From("T:Foo"));
        card.Should().NotBeNull();
        card!.FullyQualifiedName.Should().Be("Foo");
    }

    [Fact]
    public async Task Refresh_DeleteMethod_OverlayMarksSymbolDeleted()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var deletedId = SymbolId.From("T:OldFoo");
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new OverlayDelta(
                     ReindexedFiles: [],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [deletedId],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1));

        await _manager.RefreshOverlayAsync(Repo, WsId, [FilePath.From("src/OldFoo.cs")]);

        var deleted = await _overlay.GetDeletedSymbolIdsAsync(Repo, WsId);
        deleted.Should().Contain(deletedId);
    }

    [Fact]
    public async Task Refresh_AddMethod_OverlayContainsNewSymbol()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var changedFile = FilePath.From("src/NewClass.cs");
        var newSymbol = MakeSymbol("T:NewClass", changedFile);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile(FileId: "newfile1", Path: changedFile, Sha256Hash: "cafebabe", ProjectName: null)],
                     AddedOrUpdatedSymbols: [newSymbol],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1));

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, [changedFile]);

        result.IsSuccess.Should().BeTrue();
        result.Value.SymbolsUpdated.Should().BeGreaterThan(0);
        var card = await _overlay.GetOverlaySymbolAsync(Repo, WsId, SymbolId.From("T:NewClass"));
        card.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_NoChanges_ReturnsZeroReindexed()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        _git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FileChange>());

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, null);

        result.Value.FilesReindexed.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_ExplicitFilePaths_OnlyReindexesThoseFiles()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var explicitFile = FilePath.From("src/Explicit.cs");
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(OverlayDelta.Empty(1));

        await _manager.RefreshOverlayAsync(Repo, WsId, [explicitFile]);

        await _compiler.Received(1).ComputeDeltaAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<FilePath>>(fps => fps.Count == 1 && fps[0] == explicitFile),
            Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Git auto-detect should NOT have been called
        await _git.DidNotReceive().GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_AutoDetect_FindsGitChanges()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var changedFile = FilePath.From("src/AutoDetected.cs");
        _git.GetChangedFilesAsync(_tempDir, Sha, Arg.Any<CancellationToken>())
            .Returns(new[] { new FileChange(changedFile, FileChangeKind.Modified) }.ToList());
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(OverlayDelta.Empty(1));

        var result = await _manager.RefreshOverlayAsync(Repo, WsId, null);

        result.Value.FilesReindexed.Should().Be(1);
        await _compiler.Received(1).ComputeDeltaAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<FilePath>>(fps => fps.Contains(changedFile)),
            Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_MultipleCalls_RevisionsIncrement()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var file = FilePath.From("src/Foo.cs");

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(ci => OverlayDelta.Empty(ci.ArgAt<int>(6) + 1));

        var r1 = await _manager.RefreshOverlayAsync(Repo, WsId, [file]);
        var r2 = await _manager.RefreshOverlayAsync(Repo, WsId, [file]);
        var r3 = await _manager.RefreshOverlayAsync(Repo, WsId, [file]);

        r1.Value.NewOverlayRevision.Should().Be(1);
        r2.Value.NewOverlayRevision.Should().Be(2);
        r3.Value.NewOverlayRevision.Should().Be(3);
    }

    [Fact]
    public async Task Refresh_CacheInvalidated_AfterRefresh()
    {
        await _manager.CreateWorkspaceAsync(Repo, WsId, Sha, SlnPath, _tempDir);
        var cacheKey = $"{Repo.Value}:{Sha.Value}:ws:{WsId.Value}:card:T:Foo";
        await _cache.SetAsync(cacheKey, new { dummy = true });
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(OverlayDelta.Empty(1));

        await _manager.RefreshOverlayAsync(Repo, WsId, [FilePath.From("src/Foo.cs")]);

        var cached = await _cache.GetAsync<object>(cacheKey);
        cached.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeSymbol(string symbolId, FilePath filePath) =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(symbolId),
            fullyQualifiedName: symbolId.TrimStart('T', ':'),
            kind: CodeMap.Core.Enums.SymbolKind.Class,
            signature: $"class {symbolId.TrimStart('T', ':')}",
            @namespace: "TestNs",
            filePath: filePath,
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: CodeMap.Core.Enums.Confidence.High);

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
