namespace CodeMap.Integration.Tests.Workflows;

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
/// M03 supervisor/orchestration workflow tests (PHASE-03-09 T01).
/// Tests multi-agent workspace coordination and cache-accelerated baseline setup.
/// Uses real Roslyn-indexed SampleSolution via IndexedSampleSolutionFixture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class M03SupervisorWorkflowTests
    : IClassFixture<IndexedSampleSolutionFixture>, IDisposable
{
    private readonly IndexedSampleSolutionFixture _f;
    private readonly string _wsDir;
    private readonly OverlayStore _overlayStore;
    private readonly IIncrementalCompiler _compiler;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly MergedQueryEngine _mergedEngine;

    public M03SupervisorWorkflowTests(IndexedSampleSolutionFixture fixture)
    {
        _f = fixture;
        _wsDir = Path.Combine(Path.GetTempPath(), "codemap-m03-sup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wsDir);

        var overlayFactory = new OverlayDbFactory(_wsDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        _compiler = Substitute.For<IIncrementalCompiler>();
        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(OverlayDelta.Empty(1)));

        var git = Substitute.For<IGitService>();
        git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult(_f.Sha));
        git.GetChangedFilesAsync(
                Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var cache = new InMemoryCacheService();
        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _f.BaselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _f.QueryEngine, _overlayStore, _workspaceMgr,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_f.BaselineStore), new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_wsDir))
            try { Directory.Delete(_wsDir, recursive: true); } catch { /* best-effort */ }
    }

    private RoutingContext CommittedRouting() => _f.CommittedRouting();

    private RoutingContext WorkspaceRouting(WorkspaceId wsId) =>
        new(repoId: _f.RepoId, workspaceId: wsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: _f.Sha);

    // ── Workflow 6: "Multi-agent coordination" ────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_MultiAgentCoordination()
    {
        var agent1 = WorkspaceId.From("m03-agent-1");
        var agent2 = WorkspaceId.From("m03-agent-2");
        const string FakeSln = "/fake/solution.sln";
        var overlayFile = FilePath.From("SampleApp.Api/Controllers/OverlayController.cs");

        // 1. index.ensure_baseline (in test context: verify baseline already exists)
        var baselineExists = await _f.BaselineStore.BaselineExistsAsync(_f.RepoId, _f.Sha);
        baselineExists.Should().BeTrue("fixture must have indexed the baseline before this test");

        // 2. workspace.create("agent-1"), workspace.create("agent-2")
        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, agent1, _f.Sha, FakeSln, IndexedSampleSolutionFixture.SampleSolutionDir);
        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, agent2, _f.Sha, FakeSln, IndexedSampleSolutionFixture.SampleSolutionDir);

        // 3. workspace.list → both workspaces present, both fresh (revision=0)
        var workspaces = await _workspaceMgr.ListWorkspacesAsync(_f.RepoId);
        workspaces.Should().Contain(ws => ws.WorkspaceId == agent1,
            "agent-1 workspace must be listed after creation");
        workspaces.Should().Contain(ws => ws.WorkspaceId == agent2,
            "agent-2 workspace must be listed after creation");
        workspaces
            .Where(ws => ws.WorkspaceId == agent1 || ws.WorkspaceId == agent2)
            .Should().AllSatisfy(ws =>
                ws.OverlayRevision.Should().Be(0, "fresh workspace has revision 0"));

        // 4. Edit file in agent-1 scope: add an overlay Route fact
        var overlaySymbolId = SymbolId.From("M:SampleApp.Api.Controllers.OverlayController.GetOverlay");
        var overlayCard = SymbolCard.CreateMinimal(
            overlaySymbolId,
            "M:SampleApp.Api.Controllers.OverlayController.GetOverlay",
            SymbolKind.Method, "public IActionResult GetOverlay()",
            "SampleApp.Api.Controllers", overlayFile, 9, 11, "public", Confidence.High);

        var overlayFact = new ExtractedFact(
            overlaySymbolId,
            StableId: null,
            Kind: FactKind.Route,
            Value: "GET /api/overlay/test",
            FilePath: overlayFile,
            LineStart: 9,
            LineEnd: 9,
            Confidence: Confidence.High);

        var extFile = new ExtractedFile("ovl001ctrl", overlayFile, new string('f', 64), null);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [extFile],
                     AddedOrUpdatedSymbols: [overlayCard],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1,
                     Facts: [overlayFact])));

        await _workspaceMgr.RefreshOverlayAsync(_f.RepoId, agent1, [overlayFile]);

        // 5. surfaces.list_endpoints(workspace_id: agent-1) → overlay endpoint visible
        var wsEndpoints = await _mergedEngine.ListEndpointsAsync(
            WorkspaceRouting(agent1), pathFilter: null, httpMethod: null, limit: 50);
        wsEndpoints.IsSuccess.Should().BeTrue();
        wsEndpoints.Value.Data.Endpoints.Should().Contain(
            e => e.RoutePath == "/api/overlay/test",
            "workspace agent-1 overlay should expose the newly added endpoint");

        // 6. surfaces.list_endpoints() → committed mode, no overlay endpoint
        var committedEndpoints = await _mergedEngine.ListEndpointsAsync(
            CommittedRouting(), pathFilter: null, httpMethod: null, limit: 50);
        committedEndpoints.IsSuccess.Should().BeTrue();
        committedEndpoints.Value.Data.Endpoints.Should().NotContain(
            e => e.RoutePath == "/api/overlay/test",
            "committed mode must not show workspace-only overlay endpoints");

        // 7. workspace.delete("agent-1"), workspace.delete("agent-2")
        await _workspaceMgr.DeleteWorkspaceAsync(_f.RepoId, agent1);
        await _workspaceMgr.DeleteWorkspaceAsync(_f.RepoId, agent2);

        // 8. workspace.list → both agents gone
        var remaining = await _workspaceMgr.ListWorkspacesAsync(_f.RepoId);
        remaining.Should().NotContain(ws => ws.WorkspaceId == agent1,
            "deleted workspace agent-1 must not appear in list");
        remaining.Should().NotContain(ws => ws.WorkspaceId == agent2,
            "deleted workspace agent-2 must not appear in list");
    }

    // ── Workflow 7: "Cache-accelerated setup" ─────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_CacheAcceleratedSetup()
    {
        var tempLocalDir = Path.Combine(_wsDir, "cache-local");
        var tempCacheDir = Path.Combine(_wsDir, "cache-shared");
        Directory.CreateDirectory(tempLocalDir);
        Directory.CreateDirectory(tempCacheDir);

        // 1. Use fixture's indexed baseline as the "locally built" result.
        //    Push it to the shared cache directory.
        var srcFactory = new BaselineDbFactory(_f.BaselineDir, NullLogger<BaselineDbFactory>.Instance);
        var pushManager = new BaselineCacheManager(
            srcFactory, tempCacheDir, NullLogger<BaselineCacheManager>.Instance);
        await pushManager.PushAsync(_f.RepoId, _f.Sha);

        // 2. Verify baseline is now in the cache
        var inCache = await pushManager.ExistsInCacheAsync(_f.RepoId, _f.Sha);
        inCache.Should().BeTrue("push must copy the DB into the shared cache directory");

        // 3. Simulate "no local baseline": use a fresh empty local dir
        SqliteConnection.ClearAllPools();
        var pullFactory = new BaselineDbFactory(tempLocalDir, NullLogger<BaselineDbFactory>.Instance);
        var pullManager = new BaselineCacheManager(
            pullFactory, tempCacheDir, NullLogger<BaselineCacheManager>.Instance);

        var localPath = pullFactory.GetDbPath(_f.RepoId, _f.Sha);
        File.Exists(localPath).Should().BeFalse("new temp local dir must not have the baseline yet");

        // 4. index.ensure_baseline (cache hit path) → pull from cache
        var pulledPath = await pullManager.PullAsync(_f.RepoId, _f.Sha);
        pulledPath.Should().NotBeNull("cache pull must succeed when the cache was populated");
        File.Exists(pulledPath!).Should().BeTrue("pulled DB file must exist locally after pull");

        // 5. symbols.search("OrderService") → verify pulled index is usable
        SqliteConnection.ClearAllPools();
        var pullStore = new BaselineStore(pullFactory, NullLogger<BaselineStore>.Instance);
        var pullEngine = new QueryEngine(
            pullStore, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(pullStore), new GraphTraverser(),
            new FeatureTracer(pullStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var routing = new RoutingContext(repoId: _f.RepoId, baselineCommitSha: _f.Sha);
        var searchResult = await pullEngine.SearchSymbolsAsync(
            routing, "OrderService",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        searchResult.IsSuccess.Should().BeTrue("cache-pulled index must support semantic queries");
        searchResult.Value.Data.Hits.Should().NotBeEmpty(
            "OrderService must be findable in the cache-pulled index");
    }
}
