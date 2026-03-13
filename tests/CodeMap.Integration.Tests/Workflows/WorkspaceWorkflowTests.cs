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
/// Cross-cutting workspace workflow tests (PHASE-02-07 T01).
/// Exercises workspace create → edit (mock overlay delta) → refresh → query → reset lifecycles.
/// Uses real BaselineStore + OverlayStore + WorkspaceManager + MergedQueryEngine.
/// IncrementalCompiler is mocked to control overlay deltas (tested separately in IncrementalCompilerTests).
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkspaceWorkflowTests : IClassFixture<IndexedSampleSolutionFixture>, IDisposable
{
    private readonly IndexedSampleSolutionFixture _f;
    private readonly string _wsDir;
    private readonly OverlayStore _overlayStore;
    private readonly IIncrementalCompiler _compiler;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly MergedQueryEngine _mergedEngine;

    public WorkspaceWorkflowTests(IndexedSampleSolutionFixture fixture)
    {
        _f = fixture;
        _wsDir = Path.Combine(Path.GetTempPath(), "codemap-ws-wf-" + Guid.NewGuid().ToString("N"));
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

    // ── Routing helpers ───────────────────────────────────────────────────────

    private RoutingContext CommittedRouting() => _f.CommittedRouting();

    private RoutingContext WorkspaceRouting(WorkspaceId wsId) =>
        new(repoId: _f.RepoId, workspaceId: wsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: _f.Sha);

    private RoutingContext EphemeralRouting(WorkspaceId wsId, IReadOnlyList<VirtualFile>? vf = null) =>
        new(repoId: _f.RepoId, workspaceId: wsId,
            consistency: ConsistencyMode.Ephemeral, baselineCommitSha: _f.Sha,
            virtualFiles: vf);

    // ── Workflow 7: "Edit and verify" ─────────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_EditAndVerify()
    {
        var wsId = WorkspaceId.From("ws-edit-verify");
        var newFile = FilePath.From("SampleApp/Services/NewBillingService.cs");
        var newSymId = SymbolId.From("T:SampleApp.Services.NewBillingService");

        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
            IndexedSampleSolutionFixture.SampleSolutionDir);

        var card = SymbolCard.CreateMinimal(
            newSymId, "T:SampleApp.Services.NewBillingService",
            SymbolKind.Class, "class NewBillingService", "SampleApp.Services",
            newFile, 1, 5, "public", Confidence.High);
        var file = new ExtractedFile("nbsvc001", newFile, new string('a', 64), null);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [file],
                     AddedOrUpdatedSymbols: [card],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(_f.RepoId, wsId, [newFile]);

        // Workspace mode: new symbol found
        var wsSearch = await _mergedEngine.SearchSymbolsAsync(
            WorkspaceRouting(wsId), "NewBillingService", null, null);
        wsSearch.IsSuccess.Should().BeTrue();
        wsSearch.Value.Data.Hits.Should().Contain(h => h.SymbolId == newSymId,
            "workspace-mode search must include overlay symbols");

        // Committed mode: new symbol NOT found
        var commitSearch = await _mergedEngine.SearchSymbolsAsync(
            CommittedRouting(), "NewBillingService", null, null);
        commitSearch.IsSuccess.Should().BeTrue();
        commitSearch.Value.Data.Hits.Should().NotContain(h => h.SymbolId == newSymId,
            "committed-mode search must not see overlay symbols");
    }

    // ── Workflow 8: "Workspace refs" ──────────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_WorkspaceRefs()
    {
        var wsId = WorkspaceId.From("ws-refs");
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");
        var callerSym = SymbolId.From("M:SampleApp.Services.OrderService.CancelAsync");

        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
            IndexedSampleSolutionFixture.SampleSolutionDir);

        // Add a new Call reference in the overlay
        var newRef = new ExtractedReference(callerSym, _f.SubmitAsyncId, RefKind.Call, changedFile, 42, 42);
        var extFile = new ExtractedFile("os001ref", changedFile, new string('b', 64), null);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [extFile],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [newRef],
                     DeletedReferenceFiles: [changedFile],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(_f.RepoId, wsId, [changedFile]);

        // Workspace mode refs count ≥ committed refs count (new ref added)
        var wsRefs = await _mergedEngine.FindReferencesAsync(
            WorkspaceRouting(wsId), _f.SubmitAsyncId, null,
            new BudgetLimits(maxResults: 50));
        wsRefs.IsSuccess.Should().BeTrue();

        var commitRefs = await _mergedEngine.FindReferencesAsync(
            CommittedRouting(), _f.SubmitAsyncId, null,
            new BudgetLimits(maxResults: 50));
        commitRefs.IsSuccess.Should().BeTrue();

        wsRefs.Value.Data.TotalCount.Should().BeGreaterThanOrEqualTo(
            commitRefs.Value.Data.TotalCount,
            "workspace mode must include at least as many refs as committed mode");
    }

    // ── Workflow 9: "Workspace type hierarchy" ───────────────────────────────

    [Fact]
    public async Task E2E_Workflow_WorkspaceTypeHierarchy()
    {
        var wsId = WorkspaceId.From("ws-hierarchy-wf");
        var iNewIface = SymbolId.From("T:SampleApp.Services.INewWorkflowInterface");
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
            IndexedSampleSolutionFixture.SampleSolutionDir);

        var relation = new ExtractedTypeRelation(
            _f.OrderServiceId, iNewIface, TypeRelationKind.Interface, "INewWorkflowInterface");
        var extFile = new ExtractedFile("os001hier", changedFile, new string('c', 64), null);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [extFile],
                     AddedOrUpdatedSymbols: [],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     TypeRelations: [relation],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(_f.RepoId, wsId, [changedFile]);

        // Workspace mode: new interface appears in hierarchy
        var wsHier = await _mergedEngine.GetTypeHierarchyAsync(
            WorkspaceRouting(wsId), _f.OrderServiceId);
        wsHier.IsSuccess.Should().BeTrue();
        wsHier.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == iNewIface,
            "overlay type relation must appear in workspace-mode hierarchy");

        // Committed mode: interface NOT visible
        var commitHier = await _mergedEngine.GetTypeHierarchyAsync(
            CommittedRouting(), _f.OrderServiceId);
        commitHier.IsSuccess.Should().BeTrue();
        commitHier.Value.Data.Interfaces.Should().NotContain(r => r.SymbolId == iNewIface,
            "committed mode must not show overlay type relations");
    }

    // ── Workflow 10: "Virtual file span read" ─────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_VirtualFileSpanRead()
    {
        var wsId = WorkspaceId.From("ws-virtual-wf");
        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
            IndexedSampleSolutionFixture.SampleSolutionDir);

        var fp = _f.OrderServiceFilePath;
        var virtualContent = "// ephemeral line 1\n// ephemeral line 2\n// ephemeral line 3\n";
        var vf = new List<VirtualFile> { new(fp, virtualContent) };

        // With virtual files: returns virtual content
        var ephResult = await _mergedEngine.GetSpanAsync(
            EphemeralRouting(wsId, vf), fp, 1, 2, 0, null);
        ephResult.IsSuccess.Should().BeTrue();
        ephResult.Value.Data.Content.Should().Contain("ephemeral",
            "ephemeral routing with virtual_files must return virtual content");

        // Without virtual files: returns disk content (real OrderService.cs)
        var diskResult = await _mergedEngine.GetSpanAsync(
            WorkspaceRouting(wsId), fp, 1, 5, 0, null);
        diskResult.IsSuccess.Should().BeTrue();
        diskResult.Value.Data.Content.Should().NotContain("ephemeral",
            "workspace routing without virtual_files must return disk content");
    }

    // ── Workflow 11: "Workspace reset clears overlay" ─────────────────────────

    [Fact]
    public async Task E2E_Workflow_ResetClearsOverlay()
    {
        var wsId = WorkspaceId.From("ws-reset-wf");
        var newFile = FilePath.From("SampleApp/Services/ResetTargetService.cs");
        var symId = SymbolId.From("T:SampleApp.Services.ResetTargetService");

        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
            IndexedSampleSolutionFixture.SampleSolutionDir);

        var card = SymbolCard.CreateMinimal(
            symId, "T:SampleApp.Services.ResetTargetService",
            SymbolKind.Class, "class ResetTargetService", "SampleApp.Services",
            newFile, 1, 5, "public", Confidence.High);
        var file = new ExtractedFile("rst001", newFile, new string('d', 64), null);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [file],
                     AddedOrUpdatedSymbols: [card],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(_f.RepoId, wsId, [newFile]);

        // Before reset: symbol found in workspace mode
        var wsRouting = WorkspaceRouting(wsId);
        var beforeReset = await _mergedEngine.SearchSymbolsAsync(wsRouting, "ResetTargetService", null, null);
        beforeReset.IsSuccess.Should().BeTrue();
        beforeReset.Value.Data.Hits.Should().Contain(h => h.SymbolId == symId);

        // Reset the workspace
        await _workspaceMgr.ResetWorkspaceAsync(_f.RepoId, wsId);

        // After reset: symbol no longer in workspace mode
        var afterReset = await _mergedEngine.SearchSymbolsAsync(wsRouting, "ResetTargetService", null, null);
        afterReset.IsSuccess.Should().BeTrue();
        afterReset.Value.Data.Hits.Should().NotContain(h => h.SymbolId == symId,
            "reset must clear the overlay so overlay-only symbols disappear");
    }

    // ── Workflow 12: "Repo status after workspace" ────────────────────────────

    [Fact]
    public async Task E2E_Workflow_RepoStatusAfterWorkspace()
    {
        var wsId = WorkspaceId.From("ws-status-wf");

        // 1. create workspace
        await _workspaceMgr.CreateWorkspaceAsync(
            _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
            IndexedSampleSolutionFixture.SampleSolutionDir);

        // 2. refresh overlay (mock returns empty delta with revision=1)
        await _workspaceMgr.RefreshOverlayAsync(
            _f.RepoId, wsId, [FilePath.From("SampleApp/Services/OrderService.cs")]);

        // 3. check workspace list
        var workspaces = await _workspaceMgr.ListWorkspacesAsync(_f.RepoId);
        var ws = workspaces.FirstOrDefault(w => w.WorkspaceId == wsId);
        ws.Should().NotBeNull("workspace must appear in list after create");
        ws!.OverlayRevision.Should().Be(1, "one refresh increments revision to 1");

        // 4. baseline still exists after workspace operations
        var baselineExists = await _f.BaselineStore.BaselineExistsAsync(_f.RepoId, _f.Sha);
        baselineExists.Should().BeTrue("workspace operations must not affect the baseline");
    }
}
