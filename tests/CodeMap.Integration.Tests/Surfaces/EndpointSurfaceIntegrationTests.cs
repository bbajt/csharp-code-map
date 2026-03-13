namespace CodeMap.Integration.Tests.Surfaces;

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
/// Integration tests for surfaces.list_endpoints.
/// Uses manually seeded BaselineStore + OverlayStore — no Roslyn compilation.
/// Validates filtering, workspace overlay merge, and response structure.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EndpointSurfaceIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("surfaces-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-surfaces-int-01");

    // Files
    private static readonly FilePath ControllerFile = FilePath.From("src/OrdersController.cs");
    private static readonly FilePath NewControllerFile = FilePath.From("src/ProductsController.cs");

    // Symbols
    private static readonly SymbolId GetAllSym = SymbolId.From("M:MyApi.OrdersController.GetAll");
    private static readonly SymbolId GetByIdSym = SymbolId.From("M:MyApi.OrdersController.GetById(System.Int32)");
    private static readonly SymbolId CreateSym = SymbolId.From("M:MyApi.OrdersController.Create");
    private static readonly SymbolId DeleteSym = SymbolId.From("M:MyApi.OrdersController.Delete(System.Int32)");
    private static readonly SymbolId NewGetSym = SymbolId.From("M:MyApi.ProductsController.GetAll");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public EndpointSurfaceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-surf-int-" + Guid.NewGuid().ToString("N"));
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
        // Write stub source files
        File.WriteAllText(Path.Combine(_repoDir, "src", "OrdersController.cs"), "// stub");

        var data = new CompilationResult(
            Symbols: [
                MakeCard(GetAllSym,  ControllerFile),
                MakeCard(GetByIdSym, ControllerFile),
                MakeCard(CreateSym,  ControllerFile),
                MakeCard(DeleteSym,  ControllerFile),
            ],
            References: [],
            Files: [new ExtractedFile("file001", ControllerFile, new string('a', 64), null)],
            Stats: new IndexStats(4, 0, 1, 0, Confidence.High),
            TypeRelations: [],
            Facts: [
                MakeFact(GetAllSym,  "GET /api/orders",      ControllerFile, 5),
                MakeFact(GetByIdSym, "GET /api/orders/{id}", ControllerFile, 10),
                MakeFact(CreateSym,  "POST /api/orders",     ControllerFile, 15),
                MakeFact(DeleteSym,  "DELETE /api/orders/{id}", ControllerFile, 20),
            ]);

        await _baselineStore.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static SymbolCard MakeCard(SymbolId id, FilePath file) =>
        SymbolCard.CreateMinimal(
            symbolId: id, fullyQualifiedName: id.Value,
            kind: SymbolKind.Method, signature: id.Value + "()",
            @namespace: "MyApi", filePath: file,
            spanStart: 1, spanEnd: 30,
            visibility: "public", confidence: Confidence.High);

    private static ExtractedFact MakeFact(
        SymbolId symbolId, string value, FilePath file, int line) =>
        new(SymbolId: symbolId,
            StableId: null,
            Kind: FactKind.Route,
            Value: value,
            FilePath: file,
            LineStart: line,
            LineEnd: line + 4,
            Confidence: Confidence.High);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_ListEndpoints_ReturnsBaselineEndpoints()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListEndpointsAsync(routing, null, null, 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Endpoints.Should().HaveCount(4);
        result.Value.Data.Endpoints.Should().Contain(e =>
            e.HttpMethod == "GET" && e.RoutePath == "/api/orders");
    }

    [Fact]
    public async Task E2E_ListEndpoints_PathFilter_FiltersCorrectly()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListEndpointsAsync(routing, "/api/orders/{id}", null, 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Endpoints.Should().OnlyContain(e =>
            e.RoutePath.StartsWith("/api/orders/{id}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task E2E_ListEndpoints_HttpMethodFilter_FiltersCorrectly()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListEndpointsAsync(routing, null, "GET", 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Endpoints.Should().OnlyContain(e =>
            string.Equals(e.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task E2E_ListEndpoints_WorkspaceMode_IncludesOverlayEndpoints()
    {
        await SeedBaselineAsync();

        // Write stub file for new controller
        File.WriteAllText(Path.Combine(_repoDir, "src", "ProductsController.cs"), "// stub");

        // Set up compiler mock to produce a delta with the new endpoint fact
        var newCard = MakeCard(NewGetSym, NewControllerFile);
        var overlayDelta = new OverlayDelta(
            ReindexedFiles: [new ExtractedFile("file002", NewControllerFile, new string('b', 64), null)],
            AddedOrUpdatedSymbols: [newCard],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            Facts: [MakeFact(NewGetSym, "GET /api/products", NewControllerFile, 5)]);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(overlayDelta));

        // Create and refresh workspace through WorkspaceManager
        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);
        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [NewControllerFile]);

        var routing = new RoutingContext(
            repoId: Repo, workspaceId: WsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _mergedEngine.ListEndpointsAsync(routing, null, null, 50);

        result.IsSuccess.Should().BeTrue();
        var endpoints = result.Value.Data.Endpoints;
        endpoints.Should().Contain(e => e.RoutePath == "/api/products",
            because: "overlay endpoint should be included");
        endpoints.Should().Contain(e => e.RoutePath == "/api/orders",
            because: "baseline endpoints should still be present");
    }

    [Fact]
    public async Task E2E_ListEndpoints_ResponseHasCorrectStructure()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListEndpointsAsync(routing, null, null, 50);

        result.IsSuccess.Should().BeTrue();
        var endpoints = result.Value.Data.Endpoints;
        foreach (var ep in endpoints)
        {
            ep.HttpMethod.Should().NotBeNullOrEmpty();
            ep.RoutePath.Should().NotBeNullOrEmpty();
            ep.HandlerSymbol.Value.Should().NotBeNullOrEmpty();
            ep.FilePath.Value.Should().NotBeNullOrEmpty();
            ep.Line.Should().BeGreaterThan(0);
        }
    }
}
