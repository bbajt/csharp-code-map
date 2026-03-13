namespace CodeMap.Integration.Tests.Facts;

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
/// Integration tests for SymbolCard.Facts hydration (PHASE-03-05).
/// Uses manually seeded BaselineStore + OverlayStore — no Roslyn compilation.
/// Validates that GetSymbolCardAsync populates Facts from the facts table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SymbolCardFactsIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("card-facts-int-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('f', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-card-facts-01");

    private static readonly FilePath StartupFile = FilePath.From("src/Startup.cs");
    private static readonly FilePath ControllerFile = FilePath.From("src/OrdersController.cs");

    private static readonly SymbolId ConfigureSym = SymbolId.From("M:App.Startup.Configure");
    private static readonly SymbolId GetOrdersSym = SymbolId.From("M:App.Controllers.OrdersController.GetOrders");
    private static readonly SymbolId PlainSym = SymbolId.From("M:App.Services.OrderService.ProcessOrder");

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public SymbolCardFactsIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-card-facts-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        _queryEngine = new QueryEngine(
            _baselineStore, cache, tracker,
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            new FeatureTracer(_baselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var gitSvc = Substitute.For<IGitService>();
        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, gitSvc, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceMgr, cache, tracker,
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private async Task SeedBaselineAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "src", "Startup.cs"), "// stub");
        File.WriteAllText(Path.Combine(_repoDir, "src", "OrdersController.cs"), "// stub");

        var data = new CompilationResult(
            Symbols: [
                MakeCard(ConfigureSym, StartupFile),
                MakeCard(GetOrdersSym, ControllerFile),
                MakeCard(PlainSym,     StartupFile),
            ],
            References: [],
            Files: [
                new ExtractedFile("file001", StartupFile,   new string('a', 64), null),
                new ExtractedFile("file002", ControllerFile, new string('b', 64), null),
            ],
            Stats: new IndexStats(3, 0, 2, 0, Confidence.High),
            TypeRelations: [],
            Facts: [
                MakeFact(ConfigureSym, FactKind.DiRegistration,
                    "IOrderService \u2192 OrderService|Scoped",  StartupFile, 10),
                MakeFact(GetOrdersSym, FactKind.Route,
                    "GET /api/orders",                            ControllerFile, 20),
                // PlainSym has no facts
            ]);

        await _baselineStore.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static SymbolCard MakeCard(SymbolId id, FilePath file) =>
        SymbolCard.CreateMinimal(
            symbolId: id, fullyQualifiedName: id.Value,
            kind: SymbolKind.Method, signature: id.Value + "()",
            @namespace: "App", filePath: file,
            spanStart: 1, spanEnd: 30,
            visibility: "public", confidence: Confidence.High);

    private static ExtractedFact MakeFact(
        SymbolId symbolId, FactKind kind, string value, FilePath file, int line) =>
        new(SymbolId: symbolId,
            StableId: null,
            Kind: kind,
            Value: value,
            FilePath: file,
            LineStart: line,
            LineEnd: line,
            Confidence: Confidence.High);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_GetCard_SymbolWithDiFact_HasDiRegistrationFact()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.GetSymbolCardAsync(routing, ConfigureSym);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Facts.Should().HaveCount(1);
        result.Value.Data.Facts[0].Kind.Should().Be(FactKind.DiRegistration);
        result.Value.Data.Facts[0].Value.Should().Contain("IOrderService");
    }

    [Fact]
    public async Task E2E_GetCard_ControllerMethod_HasRouteFact()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.GetSymbolCardAsync(routing, GetOrdersSym);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Facts.Should().HaveCount(1);
        result.Value.Data.Facts[0].Kind.Should().Be(FactKind.Route);
        result.Value.Data.Facts[0].Value.Should().Contain("GET /api/orders");
    }

    [Fact]
    public async Task E2E_GetCard_RegularMethod_NoFacts()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.GetSymbolCardAsync(routing, PlainSym);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task E2E_GetCard_WorkspaceMode_FactsFromOverlay()
    {
        await SeedBaselineAsync();

        // Create workspace with a new DI fact in overlay
        var newFact = MakeFact(ConfigureSym, FactKind.DiRegistration,
            "INewService \u2192 NewImpl|Transient", StartupFile, 15);
        var overlayDelta = new OverlayDelta(
            ReindexedFiles: [new ExtractedFile("file001", StartupFile, new string('c', 64), null)],
            AddedOrUpdatedSymbols: [MakeCard(ConfigureSym, StartupFile)],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            Facts: [newFact]);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(overlayDelta));

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);
        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [StartupFile]);

        var routing = new RoutingContext(
            repoId: Repo, workspaceId: WsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _mergedEngine.GetSymbolCardAsync(routing, ConfigureSym);

        result.IsSuccess.Should().BeTrue();
        // Overlay fact (INewService) should be present
        result.Value.Data.Facts.Should().Contain(f =>
            f.Kind == FactKind.DiRegistration && f.Value.Contains("INewService"),
            because: "overlay DI fact should appear in card");
    }
}
