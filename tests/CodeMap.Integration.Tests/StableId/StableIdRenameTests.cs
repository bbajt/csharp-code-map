namespace CodeMap.Integration.Tests.StableId;

using CodeMap.Core.Enums;
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
/// Integration tests for SSID rename / move scenarios (PHASE-03-01).
/// AC-T05-02: Rename produces a single merged entry, not a duplicate.
/// AC-T05-03: Renamed symbol shares the same stable_id as the original.
/// Uses real BaselineStore + OverlayStore + WorkspaceManager + MergedQueryEngine.
/// Mocks IIncrementalCompiler to control deltas without Roslyn.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StableIdRenameTests : IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly RepoId Repo = RepoId.From("rename-test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-rename");
    private static readonly FilePath ServiceFile = FilePath.From("src/OrderService.cs");

    // Original symbol
    private static readonly SymbolId OriginalId = SymbolId.From("T:SampleApp.Services.OrderService");
    private static readonly StableId Stable = new("sym_" + new string('d', 16));

    // Renamed symbol (same file, same structure — stable_id should be equal)
    private static readonly SymbolId RenamedId = SymbolId.From("T:SampleApp.Services.OrderServiceV2");

    // ── Infrastructure ────────────────────────────────────────────────────────

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "codemap-rename-" + Guid.NewGuid().ToString("N"));

    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly WorkspaceManager _workspaceManager;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public StableIdRenameTests()
    {
        Directory.CreateDirectory(_tempDir);
        var repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, "src"));
        File.WriteAllText(Path.Combine(repoDir, "src", "OrderService.cs"),
            "namespace SampleApp.Services;\npublic class OrderServiceV2 {}");

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        // Seed baseline with original symbol (with stable_id)
        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);
        var originalCard = MakeCard(OriginalId, "OrderService", Stable);
        var fileEntry = new ExtractedFile("file001", ServiceFile, new string('a', 64), "SampleApp");
        var compilation = new CompilationResult(
            [originalCard], [],
            [fileEntry],
            new IndexStats(1, 0, 1, 0.01, Confidence.High));
        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, repoDir).GetAwaiter().GetResult();

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var cache = new InMemoryCacheService();
        _workspaceManager = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
        _workspaceManager.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", repoDir)
                         .GetAwaiter().GetResult();

        var queryEngine = new QueryEngine(
            _baselineStore, cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            new FeatureTracer(_baselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        _mergedEngine = new MergedQueryEngine(
            queryEngine, _overlayStore, _workspaceManager,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext WorkspaceRouting() =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static SymbolCard MakeCard(SymbolId symId, string name, StableId? stableId = null) =>
        SymbolCard.CreateMinimal(
            symbolId: symId,
            fullyQualifiedName: name,
            kind: SymbolKind.Class,
            signature: $"public class {name}",
            @namespace: "SampleApp.Services",
            filePath: ServiceFile,
            spanStart: 1,
            spanEnd: 5,
            visibility: "public",
            confidence: Confidence.High) with
        { StableId = stableId };

    private void SetupRenameOverlay()
    {
        // Overlay delta: delete original, add renamed (with same stable_id)
        var renamedCard = MakeCard(RenamedId, "OrderServiceV2", Stable);
        var renamedFile = new ExtractedFile("file001", ServiceFile, new string('b', 64), "SampleApp");
        var delta = new OverlayDelta(
            ReindexedFiles: [renamedFile],
            AddedOrUpdatedSymbols: [renamedCard],
            DeletedSymbolIds: [OriginalId],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            TypeRelations: []);

        _compiler.ComputeDeltaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(delta));
    }

    // ── AC-T05-02: Rename produces single merged entry, not a duplicate ────────

    [Fact]
    public async Task E2E_Rename_WorkspaceOverlay_MergesCorrectly()
    {
        SetupRenameOverlay();
        await _workspaceManager.RefreshOverlayAsync(Repo, WsId, [ServiceFile], CancellationToken.None);

        var routing = WorkspaceRouting();

        // New name must be found
        var newSearch = await _mergedEngine.SearchSymbolsAsync(
            routing, "OrderServiceV2",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]), null);
        newSearch.IsSuccess.Should().BeTrue();
        newSearch.Value.Data.Hits.Should().NotBeEmpty(
            because: "renamed symbol OrderServiceV2 should be found in workspace");

        // Old name must NOT be found (overlay deleted it)
        var oldSearch = await _mergedEngine.SearchSymbolsAsync(
            routing, "OrderService",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]), null);
        var hits = oldSearch.IsSuccess ? oldSearch.Value.Data.Hits : [];
        hits.Should().NotContain(h => h.SymbolId == OriginalId,
            because: "original OrderService should be deleted from overlay");

        // Total hits for the file should be exactly 1 (no duplicate)
        var allHits = (newSearch.IsSuccess ? newSearch.Value.Data.Hits : [])
            .Where(h => h.FilePath == ServiceFile)
            .ToList();
        allHits.Should().HaveCount(1, because: "rename must produce exactly one entry, not a duplicate");
    }

    // ── AC-T05-03: stable_id is preserved across rename ───────────────────────

    [Fact]
    public async Task E2E_Rename_StableIdPreserved()
    {
        SetupRenameOverlay();
        await _workspaceManager.RefreshOverlayAsync(Repo, WsId, [ServiceFile], CancellationToken.None);

        var routing = WorkspaceRouting();

        // Get card for renamed symbol
        var cardResult = await _mergedEngine.GetSymbolCardAsync(routing, RenamedId);
        cardResult.IsSuccess.Should().BeTrue(
            because: $"GetSymbolCard for renamed {RenamedId.Value} must succeed");

        cardResult.Value.Data.StableId.Should().NotBeNull(
            because: "renamed symbol must retain its stable_id");
        cardResult.Value.Data.StableId!.Value.Should().Be(Stable,
            because: "stable_id must be identical before and after rename");
    }

    // ── GetSymbolByStableId resolves renamed symbol via stable_id ─────────────

    [Fact]
    public async Task E2E_GetCardByStableId_RenamedSymbol_ReturnsNewCard()
    {
        SetupRenameOverlay();
        await _workspaceManager.RefreshOverlayAsync(Repo, WsId, [ServiceFile], CancellationToken.None);

        var routing = WorkspaceRouting();

        // Look up by stable_id (which hasn't changed)
        var result = await _mergedEngine.GetSymbolByStableIdAsync(routing, Stable);

        result.IsSuccess.Should().BeTrue(
            because: "GetSymbolByStableIdAsync must resolve the renamed symbol via stable_id");
        result.Value.Data.SymbolId.Should().Be(RenamedId,
            because: "stable_id lookup must return the renamed symbol, not the deleted original");
    }

    // ── Committed mode still resolves original via stable_id ─────────────────

    [Fact]
    public async Task E2E_GetCardByStableId_CommittedMode_ReturnsOriginal()
    {
        // No overlay applied — committed mode should find the original symbol
        var routing = CommittedRouting();

        var result = await _mergedEngine.GetSymbolByStableIdAsync(routing, Stable);

        result.IsSuccess.Should().BeTrue(
            because: "GetSymbolByStableIdAsync in committed mode must find the baseline symbol");
        result.Value.Data.SymbolId.Should().Be(OriginalId);
    }
}
