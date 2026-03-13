namespace CodeMap.Integration.Tests.Workspace;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// End-to-end integration tests for workspace-mode query merge (PHASE-02-03).
/// Uses real BaselineStore + OverlayStore + WorkspaceManager + MergedQueryEngine.
/// Mocks IIncrementalCompiler to control overlay deltas without Roslyn.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MergedQueryIntegrationTests : IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly RepoId Repo = RepoId.From("merge-query-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));
    private static readonly WorkspaceId WsA = WorkspaceId.From("ws-merge-A");
    private static readonly WorkspaceId WsB = WorkspaceId.From("ws-merge-B");

    // Two source files in the baseline
    private static readonly FilePath FileA = FilePath.From("src/ServiceA.cs");
    private static readonly FilePath FileB = FilePath.From("src/ServiceB.cs");

    // Two baseline symbols
    private const string SymAId = "T:ServiceA";
    private const string SymBId = "T:ServiceB";

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "codemap-merge-query-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoRootDir;

    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly WorkspaceManager _workspaceManager;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public MergedQueryIntegrationTests()
    {
        Directory.CreateDirectory(_tempDir);
        _repoRootDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoRootDir, "src"));

        // Write real source files so GetSpanAsync / GetDefinitionSpanAsync can read them
        File.WriteAllText(
            Path.Combine(_repoRootDir, "src", "ServiceA.cs"),
            string.Join("\n", Enumerable.Range(1, 40).Select(i =>
                i == 5 ? "public class ServiceA {" :
                i == 10 ? "    public void OriginalMethod() {}" :
                i == 20 ? "    public void UpdatedMethod() {}" :
                i == 39 ? "}" :
                $"    // line {i}")));
        File.WriteAllText(
            Path.Combine(_repoRootDir, "src", "ServiceB.cs"),
            string.Join("\n", Enumerable.Range(1, 20).Select(i =>
                i == 5 ? "public class ServiceB {}" :
                $"// line {i}")));

        // Baseline store
        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        // Seed baseline with 2 symbols from 2 different files
        var symA = MakeSymbol(SymAId, "ServiceA", FileA, spanStart: 5, spanEnd: 39);
        var symB = MakeSymbol(SymBId, "ServiceB", FileB, spanStart: 5, spanEnd: 20);
        var fileA = new ExtractedFile(FileId: "fileA001", Path: FileA, Sha256Hash: new string('a', 64), ProjectName: null);
        var fileB = new ExtractedFile(FileId: "fileB001", Path: FileB, Sha256Hash: new string('b', 64), ProjectName: null);
        var compilation = new CompilationResult(
            Symbols: [symA, symB],
            References: [],
            Files: [fileA, fileB],
            Stats: new IndexStats(2, 0, 2, 0.1, Confidence.High));
        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, _repoRootDir).GetAwaiter().GetResult();

        // Overlay store
        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        // Git mock (used for auto-detect changed files; unused in most tests)
        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var cache = new InMemoryCacheService();
        _workspaceManager = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _queryEngine = new QueryEngine(
            _baselineStore, cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), new FeatureTracer(_baselineStore, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceManager,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);

        // Default compiler: returns empty delta
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(OverlayDelta.Empty(1)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext WorkspaceRouting(WorkspaceId wsId) =>
        new(repoId: Repo, workspaceId: wsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static SymbolCard MakeSymbol(
        string id, string name, FilePath file, int spanStart = 1, int spanEnd = 10) =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(id),
            fullyQualifiedName: name,
            kind: SymbolKind.Class,
            signature: $"public class {name}",
            @namespace: "TestNs",
            filePath: file,
            spanStart: spanStart,
            spanEnd: spanEnd,
            visibility: "public",
            confidence: Confidence.High);

    private async Task CreateAndSeedWorkspace(WorkspaceId wsId)
    {
        var result = await _workspaceManager.CreateWorkspaceAsync(
            Repo, wsId, Sha, "/fake/solution.sln", _repoRootDir);
        result.IsSuccess.Should().BeTrue($"workspace {wsId.Value} creation should succeed");
    }

    // ── Search merge tests ────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Search_AfterAddingMethod_NewMethodAppearsInResults()
    {
        await CreateAndSeedWorkspace(WsA);

        var newSymbol = MakeSymbol("M:ServiceA.NewMethod", "ServiceA.NewMethod", FileA, spanStart: 10, spanEnd: 11);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [newSymbol],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        var result = await _mergedEngine.SearchSymbolsAsync(WorkspaceRouting(WsA), "NewMethod", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == "M:ServiceA.NewMethod");
    }

    [Fact]
    public async Task E2E_Search_AfterDeletingMethod_RemovedMethodAbsent()
    {
        await CreateAndSeedWorkspace(WsA);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [SymbolId.From(SymAId)],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        var result = await _mergedEngine.SearchSymbolsAsync(WorkspaceRouting(WsA), "ServiceA", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotContain(h => h.SymbolId.Value == SymAId);
    }

    [Fact]
    public async Task E2E_Search_UnmodifiedSymbol_StillReturnsFromBaseline()
    {
        await CreateAndSeedWorkspace(WsA);

        // Modify FileA only — FileB (ServiceB) is untouched
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [MakeSymbol(SymAId, "ServiceA_Updated", FileA)],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        // ServiceB lives in FileB which was NOT reindexed
        var result = await _mergedEngine.SearchSymbolsAsync(WorkspaceRouting(WsA), "ServiceB", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == SymBId);
    }

    [Fact]
    public async Task E2E_Search_CommittedMode_IgnoresOverlay()
    {
        await CreateAndSeedWorkspace(WsA);

        var newSymbol = MakeSymbol("M:ServiceA.OverlayOnly", "OverlayOnly", FileA);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [newSymbol],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        // Committed mode — should NOT see overlay-only symbol
        var result = await _mergedEngine.SearchSymbolsAsync(CommittedRouting(), "OverlayOnly", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotContain(h => h.SymbolId.Value == "M:ServiceA.OverlayOnly");
    }

    // ── GetCard merge tests ───────────────────────────────────────────────────

    [Fact]
    public async Task E2E_GetCard_ModifiedSymbol_ReturnsOverlayVersion()
    {
        await CreateAndSeedWorkspace(WsA);

        var updatedCard = MakeSymbol(SymAId, "ServiceA_Renamed", FileA);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [updatedCard],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        var result = await _mergedEngine.GetSymbolCardAsync(WorkspaceRouting(WsA), SymbolId.From(SymAId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.FullyQualifiedName.Should().Be("ServiceA_Renamed");
    }

    [Fact]
    public async Task E2E_GetCard_UnmodifiedSymbol_ReturnsBaselineVersion()
    {
        await CreateAndSeedWorkspace(WsA);

        // Refresh FileA — ServiceB (in FileB) is untouched
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        var result = await _mergedEngine.GetSymbolCardAsync(WorkspaceRouting(WsA), SymbolId.From(SymBId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.FullyQualifiedName.Should().Be("ServiceB");
    }

    [Fact]
    public async Task E2E_GetCard_DeletedSymbol_ReturnsNotFound()
    {
        await CreateAndSeedWorkspace(WsA);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [SymbolId.From(SymAId)],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        var result = await _mergedEngine.GetSymbolCardAsync(WorkspaceRouting(WsA), SymbolId.From(SymAId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ── Span read tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_GetSpan_ModifiedFile_ReturnsEditedContent()
    {
        await CreateAndSeedWorkspace(WsA);

        // Add a distinctive comment to the file (simulates editing)
        var fileAPath = Path.Combine(_repoRootDir, "src", "ServiceA.cs");
        File.WriteAllText(fileAPath, "// EDITED_MARKER\n" + File.ReadAllText(fileAPath));

        var result = await _mergedEngine.GetSpanAsync(
            WorkspaceRouting(WsA), FileA, startLine: 1, endLine: 3, contextLines: 0, budgets: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("EDITED_MARKER");
    }

    [Fact]
    public async Task E2E_GetDefinitionSpan_ModifiedSymbol_UsesOverlaySpanCoordinates()
    {
        await CreateAndSeedWorkspace(WsA);

        // Overlay symbol for ServiceA with different span (line 20-30 instead of 5-39)
        var overlayCard = MakeSymbol(SymAId, "ServiceA", FileA, spanStart: 20, spanEnd: 21);
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [overlayCard],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        var result = await _mergedEngine.GetDefinitionSpanAsync(
            WorkspaceRouting(WsA), SymbolId.From(SymAId), maxLines: 10, contextLines: 0);

        result.IsSuccess.Should().BeTrue();
        // Span should use overlay coordinates (line 20) not baseline (line 5)
        result.Value.Data.StartLine.Should().BeGreaterOrEqualTo(18); // ≥ line 20 - contextLines(2)
    }

    // ── Workspace isolation tests ─────────────────────────────────────────────

    [Fact]
    public async Task E2E_TwoWorkspaces_DifferentOverlays_IndependentResults()
    {
        await CreateAndSeedWorkspace(WsA);
        await CreateAndSeedWorkspace(WsB);

        // Workspace A: ServiceA is deleted
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("fileA001", FileA, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [SymbolId.From(SymAId)],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));
        await _workspaceManager.RefreshOverlayAsync(Repo, WsA, [FileA]);

        // Workspace B: ServiceA is unmodified
        // (no refresh for WsB — it sees baseline)

        var resultA = await _mergedEngine.SearchSymbolsAsync(WorkspaceRouting(WsA), "ServiceA", null, null);
        var resultB = await _mergedEngine.SearchSymbolsAsync(WorkspaceRouting(WsB), "ServiceA", null, null);

        resultA.IsSuccess.Should().BeTrue();
        resultB.IsSuccess.Should().BeTrue();

        // WsA sees ServiceA as deleted
        resultA.Value.Data.Hits.Should().NotContain(h => h.SymbolId.Value == SymAId);
        // WsB still sees ServiceA from baseline
        resultB.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == SymAId);
    }
}
