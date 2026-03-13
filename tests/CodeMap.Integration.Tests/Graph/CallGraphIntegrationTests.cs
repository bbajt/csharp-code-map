namespace CodeMap.Integration.Tests.Graph;

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
/// End-to-end integration tests for graph.callers + graph.callees (PHASE-02-05).
/// Uses manually seeded BaselineStore + real QueryEngine — no Roslyn.
///
/// Seeded call chain: Controller.Act → Service.DoWork → Repository.Save
/// </summary>
[Trait("Category", "Integration")]
public sealed class CallGraphIntegrationTests : IDisposable
{
    // ── Symbol IDs for the seeded call chain ──────────────────────────────────

    private static readonly RepoId Repo = RepoId.From("callgraph-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('9', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-callgraph-01");

    private static readonly SymbolId Controller = SymbolId.From("M:MyNs.Controller.Act");
    private static readonly SymbolId Service = SymbolId.From("M:MyNs.Service.DoWork");
    private static readonly SymbolId Repository = SymbolId.From("M:MyNs.Repository.Save");

    private static readonly FilePath ControllerFile = FilePath.From("src/Controller.cs");
    private static readonly FilePath ServiceFile = FilePath.From("src/Service.cs");
    private static readonly FilePath RepositoryFile = FilePath.From("src/Repository.cs");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public CallGraphIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-callgraph-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        // Write minimal source files (needed for ExcerptReader)
        WriteSourceFile("Controller.cs", "namespace MyNs { class Controller { void Act() {} } }");
        WriteSourceFile("Service.cs", "namespace MyNs { class Service    { void DoWork() {} } }");
        WriteSourceFile("Repository.cs", "namespace MyNs { class Repository { void Save() {} } }");

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
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

        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _queryEngine = new QueryEngine(
            _baselineStore, cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), new FeatureTracer(_baselineStore, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceMgr,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);

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

    // ── Callers tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Callers_Depth1_ReturnsDirectCallers()
    {
        // Repository.Save is called by Service.DoWork (depth 1)
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCallersAsync(routing, Repository, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Root.Should().Be(Repository);
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == Service);
        // Controller.Act is only at depth 2 — should not appear at depth 1
        result.Value.Data.Nodes.Should().NotContain(n => n.SymbolId == Controller);
    }

    [Fact]
    public async Task E2E_Callers_Depth2_ReturnsTransitiveCallers()
    {
        // Repository.Save ← Service.DoWork (depth 1) ← Controller.Act (depth 2)
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCallersAsync(routing, Repository, depth: 2, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        var nodes = result.Value.Data.Nodes;
        nodes.Should().Contain(n => n.SymbolId == Service && n.Depth == 1);
        nodes.Should().Contain(n => n.SymbolId == Controller && n.Depth == 2);
    }

    [Fact]
    public async Task E2E_Callers_NoCaller_ReturnsRootOnly()
    {
        // Controller.Act has no callers in the seeded baseline
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCallersAsync(routing, Controller, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.TotalNodesFound.Should().Be(0);
        result.Value.Data.Nodes.Should().ContainSingle(n => n.SymbolId == Controller);
    }

    [Fact]
    public async Task E2E_Callers_EdgesRecorded()
    {
        // Service.DoWork callers = [Controller.Act]
        // Root node (Service) should have Controller in its ConnectedIds (= EdgesTo for callers view)
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCallersAsync(routing, Service, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        var serviceNode = result.Value.Data.Nodes.First(n => n.SymbolId == Service);
        serviceNode.EdgesTo.Should().Contain(Controller);
    }

    // ── Callees tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Callees_Depth1_ReturnsDirectCallees()
    {
        // Controller.Act calls Service.DoWork (depth 1)
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCalleesAsync(routing, Controller, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == Service);
        result.Value.Data.Nodes.Should().NotContain(n => n.SymbolId == Repository);
    }

    [Fact]
    public async Task E2E_Callees_Depth2_ReturnsTransitiveCallees()
    {
        // Controller.Act → Service.DoWork (depth 1) → Repository.Save (depth 2)
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCalleesAsync(routing, Controller, depth: 2, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        var nodes = result.Value.Data.Nodes;
        nodes.Should().Contain(n => n.SymbolId == Service && n.Depth == 1);
        nodes.Should().Contain(n => n.SymbolId == Repository && n.Depth == 2);
    }

    // ── Workspace mode ────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Callers_WorkspaceMode_IncludesOverlayCallSites()
    {
        // Create a new symbol in the overlay that calls Repository.Save
        var newCaller = SymbolId.From("M:MyNs.NewController.Handle");
        var newCallerFile = FilePath.From("src/NewController.cs");
        WriteSourceFile("NewController.cs",
            "namespace MyNs { class NewController { void Handle() {} } }");

        var overlayRef = new ExtractedReference(
            FromSymbol: newCaller,
            ToSymbol: Repository,
            Kind: RefKind.Call,
            FilePath: newCallerFile,
            LineStart: 1,
            LineEnd: 1);

        var newCallerSymbol = MakeSymbolCard(newCaller, "Handle", SymbolKind.Method, newCallerFile, 1);

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("newctrl001", newCallerFile, new string('f', 64), null)],
                     AddedOrUpdatedSymbols: [newCallerSymbol],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [overlayRef],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [newCallerFile]);

        var wsRouting = WorkspaceRouting();
        var result = await _mergedEngine.GetCallersAsync(wsRouting, Repository, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == newCaller);
    }

    [Fact]
    public async Task E2E_Callees_CommittedMode_IgnoresOverlay()
    {
        // Even after creating a workspace with overlay refs, committed mode must not see them
        var newCaller = SymbolId.From("M:MyNs.OverlayOnly.Handle");
        var overlayRef = new ExtractedReference(
            FromSymbol: newCaller,
            ToSymbol: Repository,
            Kind: RefKind.Call,
            FilePath: FilePath.From("src/OverlayOnly.cs"),
            LineStart: 1,
            LineEnd: 1);

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("overlay001", FilePath.From("src/OverlayOnly.cs"), new string('0', 64), null)],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [overlayRef],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [FilePath.From("src/OverlayOnly.cs")]);

        // Committed mode — should not see the overlay caller
        var result = await _queryEngine.GetCallersAsync(CommittedRouting(), Repository, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Should().NotContain(n => n.SymbolId == newCaller);
    }

    [Fact]
    public async Task E2E_Callers_LimitPerLevel_Truncates()
    {
        // Repository.Save is called by both Service.DoWork AND another seeded caller
        // With limit_per_level=1, only 1 node returned and Truncated=true
        var routing = CommittedRouting();
        var result = await _queryEngine.GetCallersAsync(routing, Repository, depth: 1, limitPerLevel: 1, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Truncated.Should().BeTrue();
        result.Value.Data.TotalNodesFound.Should().Be(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static RoutingContext WorkspaceRouting() =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private void WriteSourceFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_repoDir, "src", name), content);

    private void SeedBaseline()
    {
        // Symbols
        var symController = MakeSymbolCard(Controller, "Act", SymbolKind.Method, ControllerFile, 1);
        var symService = MakeSymbolCard(Service, "DoWork", SymbolKind.Method, ServiceFile, 1);
        var symRepo = MakeSymbolCard(Repository, "Save", SymbolKind.Method, RepositoryFile, 1);

        // Files
        var fController = new ExtractedFile("ctrl001", ControllerFile, new string('1', 64), null);
        var fService = new ExtractedFile("svc001", ServiceFile, new string('2', 64), null);
        var fRepo = new ExtractedFile("repo001", RepositoryFile, new string('3', 64), null);

        // Call chain: Controller → Service → Repository
        // Also add a second caller for Repository to test limitPerLevel truncation
        var extraCaller = SymbolId.From("M:MyNs.OtherService.Process");
        var symExtra = MakeSymbolCard(extraCaller, "Process", SymbolKind.Method, ServiceFile, 3);

        var refs = new List<ExtractedReference>
        {
            // Controller.Act → Service.DoWork
            new(FromSymbol: Controller, ToSymbol: Service,    Kind: RefKind.Call,
                FilePath: ControllerFile, LineStart: 1, LineEnd: 1),
            // Service.DoWork → Repository.Save
            new(FromSymbol: Service,    ToSymbol: Repository, Kind: RefKind.Call,
                FilePath: ServiceFile,   LineStart: 1, LineEnd: 1),
            // OtherService.Process → Repository.Save (second caller for truncation test)
            new(FromSymbol: extraCaller, ToSymbol: Repository, Kind: RefKind.Call,
                FilePath: ServiceFile,   LineStart: 3, LineEnd: 3),
        };

        var compilation = new CompilationResult(
            Symbols: [symController, symService, symRepo, symExtra],
            References: refs,
            Files: [fController, fService, fRepo],
            Stats: new IndexStats(4, refs.Count, 3, 0.1, Confidence.High));

        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, _repoDir)
                      .GetAwaiter().GetResult();
    }

    private static SymbolCard MakeSymbolCard(
        SymbolId id, string name, SymbolKind kind, FilePath file, int line) =>
        SymbolCard.CreateMinimal(
            symbolId: id,
            fullyQualifiedName: id.Value,
            kind: kind,
            signature: $"void {name}()",
            @namespace: "MyNs",
            filePath: file,
            spanStart: line,
            spanEnd: line + 5,
            visibility: "public",
            confidence: Confidence.High);
}
