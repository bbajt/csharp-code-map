namespace CodeMap.Integration.Tests.Trace;

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
/// Integration tests for graph.trace_feature (PHASE-04-06).
/// Uses manually seeded BaselineStore + real QueryEngine — no Roslyn.
///
/// Seeded call chain: Controller.Get → Service.GetOrders → Repository.FindAll
/// Controller.Get has a Route fact; Service.GetOrders has a DbTable fact.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FeatureTraceIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("trace-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-trace-01");

    private static readonly SymbolId Controller = SymbolId.From("M:MyNs.Controller.Get");
    private static readonly SymbolId Service = SymbolId.From("M:MyNs.Service.GetOrders");
    private static readonly SymbolId Repository = SymbolId.From("M:MyNs.Repository.FindAll");

    private static readonly FilePath ControllerFile = FilePath.From("src/Controller.cs");
    private static readonly FilePath ServiceFile = FilePath.From("src/Service.cs");
    private static readonly FilePath RepositoryFile = FilePath.From("src/Repository.cs");

    private readonly string _tempDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public FeatureTraceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-trace-int-" + Guid.NewGuid().ToString("N"));
        var repoDir = Path.Combine(_tempDir, "repo");
        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        SeedBaseline();

        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var cache = new InMemoryCacheService();
        var traverser = new GraphTraverser();
        var featureTracer = new FeatureTracer(_baselineStore, traverser);

        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _queryEngine = new QueryEngine(
            _baselineStore, cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore),
            traverser,
            featureTracer,
            NullLogger<QueryEngine>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceMgr,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore),
            new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);

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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_TraceFeature_ControllerAction_ShowsCallTree()
    {
        var result = await _queryEngine.TraceFeatureAsync(
            CommittedRouting(), Controller, depth: 2, limit: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.EntryPoint.Should().Be(Controller);
        result.Value.Data.Nodes.Should().HaveCount(1);

        var root = result.Value.Data.Nodes[0];
        root.SymbolId.Should().Be(Controller);
        root.Children.Should().Contain(n => n.SymbolId == Service, "Service is a direct callee of Controller");
    }

    [Fact]
    public async Task E2E_TraceFeature_EntryPointHasRouteAnnotation()
    {
        var result = await _queryEngine.TraceFeatureAsync(
            CommittedRouting(), Controller, depth: 2, limit: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.EntryPointRoute.Should().Be("GET /api/orders",
            "Controller.Get has a Route fact seeded in baseline");
    }

    [Fact]
    public async Task E2E_TraceFeature_ServiceNode_HasDbTableAnnotation()
    {
        var result = await _queryEngine.TraceFeatureAsync(
            CommittedRouting(), Controller, depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var root = result.Value.Data.Nodes[0];
        var serviceNode = FindNode(root, Service);
        serviceNode.Should().NotBeNull();
        serviceNode!.Annotations.Should().Contain(a => a.Kind == "DbTable",
            "Service.GetOrders has a DbTable fact for Orders table");
    }

    [Fact]
    public async Task E2E_TraceFeature_DepthLimit_Respected()
    {
        // depth=1 → only Controller + its direct callees (Service), no deeper
        var result = await _queryEngine.TraceFeatureAsync(
            CommittedRouting(), Controller, depth: 1, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var root = result.Value.Data.Nodes[0];
        // Service is at depth 1, but Service's children (Repository) should not be included
        var serviceNode = root.Children.FirstOrDefault(n => n.SymbolId == Service);
        serviceNode.Should().NotBeNull();
        serviceNode!.Children.Should().BeEmpty("depth=1 stops at Service, Repository is at depth 2");
    }

    [Fact]
    public async Task E2E_TraceFeature_SymbolNotFound_ReturnsError()
    {
        var nonExistent = SymbolId.From("M:Nobody.Nothing");
        var result = await _queryEngine.TraceFeatureAsync(
            CommittedRouting(), nonExistent, depth: 3, limit: 100);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(Core.Errors.ErrorCodes.NotFound);
    }

    [Fact]
    public async Task E2E_TraceFeature_WorkspaceMode_IncludesOverlayFacts()
    {
        // Create workspace + add overlay fact on Controller
        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _tempDir);

        // Add an overlay delta with a config fact on Controller
        var overlayRef = new ExtractedFact(
            SymbolId: Controller,
            StableId: null,
            Kind: FactKind.Config,
            Value: "Feature:NewOrder|GetValue",
            FilePath: ControllerFile,
            LineStart: 5,
            LineEnd: 5,
            Confidence: Confidence.High);

        var delta = new OverlayDelta(
            ReindexedFiles: [new ExtractedFile("ctrl-overlay", ControllerFile, new string('f', 64), null)],
            AddedOrUpdatedSymbols: [],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            Facts: [overlayRef]);

        await _overlayStore.ApplyDeltaAsync(Repo, WsId, delta);

        var wsRouting = new RoutingContext(
            repoId: Repo, workspaceId: WsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _mergedEngine.TraceFeatureAsync(wsRouting, Controller, depth: 2, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var root = result.Value.Data.Nodes[0];
        root.Annotations.Should().Contain(a => a.Kind == "Config" && a.Value == "Feature:NewOrder",
            "overlay config fact should appear in workspace trace annotations");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static TraceNode? FindNode(TraceNode root, SymbolId target)
    {
        if (root.SymbolId == target) return root;
        foreach (var child in root.Children)
        {
            var found = FindNode(child, target);
            if (found is not null) return found;
        }
        return null;
    }

    private void SeedBaseline()
    {
        var symController = MakeCard(Controller, "Get", ControllerFile, 1);
        var symService = MakeCard(Service, "GetOrders", ServiceFile, 1);
        var symRepo = MakeCard(Repository, "FindAll", RepositoryFile, 1);

        var fController = new ExtractedFile("ctrl001", ControllerFile, new string('1', 64), null);
        var fService = new ExtractedFile("svc001", ServiceFile, new string('2', 64), null);
        var fRepo = new ExtractedFile("repo001", RepositoryFile, new string('3', 64), null);

        var refs = new List<ExtractedReference>
        {
            new(FromSymbol: Controller, ToSymbol: Service,    Kind: RefKind.Call,
                FilePath: ControllerFile, LineStart: 5, LineEnd: 5),
            new(FromSymbol: Service,    ToSymbol: Repository, Kind: RefKind.Call,
                FilePath: ServiceFile,   LineStart: 8, LineEnd: 8),
        };

        // Facts: Route on Controller, DbTable on Service
        var facts = new List<ExtractedFact>
        {
            new(SymbolId: Controller, StableId: null, Kind: FactKind.Route,
                Value: "GET /api/orders", FilePath: ControllerFile,
                LineStart: 1, LineEnd: 1, Confidence: Confidence.High),
            new(SymbolId: Service, StableId: null, Kind: FactKind.DbTable,
                Value: "Orders|DbSet<Order>", FilePath: ServiceFile,
                LineStart: 8, LineEnd: 8, Confidence: Confidence.High),
        };

        var compilation = new CompilationResult(
            Symbols: [symController, symService, symRepo],
            References: refs,
            Files: [fController, fService, fRepo],
            Facts: facts,
            Stats: new IndexStats(3, refs.Count, 3, 0.1, Confidence.High));

        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, _tempDir)
                      .GetAwaiter().GetResult();
    }

    private static SymbolCard MakeCard(SymbolId id, string name, FilePath file, int line) =>
        SymbolCard.CreateMinimal(
            symbolId: id,
            fullyQualifiedName: id.Value,
            kind: SymbolKind.Method,
            signature: $"void {name}()",
            @namespace: "MyNs",
            filePath: file,
            spanStart: line,
            spanEnd: line + 5,
            visibility: "public",
            confidence: Confidence.High);
}
