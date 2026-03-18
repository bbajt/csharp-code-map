namespace CodeMap.Integration.Tests.Resilience;

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
/// Cross-cutting integration tests verifying that CodeMap degrades gracefully
/// when compilation fails (SemanticLevel + resolution state interaction).
/// PHASE-02-08 T03.
/// Uses manually crafted CompilationResult to avoid MSBuildWorkspace overhead.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DegradedModeWorkflowTests : IAsyncLifetime
{
    // ── Static test data ──────────────────────────────────────────────────────

    private static readonly RepoId _repoId = RepoId.From("degraded-mode-test-repo");
    private static readonly CommitSha _sha = CommitSha.From(new string('d', 40));

    private const string BrokenSymbolId = "BrokenClass";
    private const string HealthySymbolId = "HealthyClass";
    private const string FilePath1 = "src/BrokenClass.cs";
    private const string FilePath2 = "src/HealthyClass.cs";

    // ── Fixture ───────────────────────────────────────────────────────────────

    private string _tempDir = null!;
    private BaselineStore _store = null!;
    private QueryEngine _engine = null!;
    private RoutingContext _routing = default!;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-degraded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();
        _engine = new QueryEngine(
            _store, cache, tracker,
            new ExcerptReader(_store), new GraphTraverser(),
            new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        _routing = new RoutingContext(repoId: _repoId, baselineCommitSha: _sha);

        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    // ── Test 1: Broken build — search still works ─────────────────────────────

    [Fact]
    public async Task E2E_BrokenBuild_SearchStillWorks_ReturnsLowConfidenceSymbols()
    {
        // Arrange: index a "broken" compilation (all syntax-only, Confidence.Low)
        var compiled = BuildSyntaxOnlyResult();
        await _store.CreateBaselineAsync(_repoId, _sha, compiled, _tempDir);

        // Act: search for a symbol by name
        var result = await _engine.SearchSymbolsAsync(
            _routing, "BrokenClass",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 10));

        // Assert: results exist; SemanticLevel == SyntaxOnly
        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotBeEmpty();
        result.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.SyntaxOnly);
    }

    // ── Test 2: Broken build — get card still works ───────────────────────────

    [Fact]
    public async Task E2E_BrokenBuild_GetCardStillWorks_WithLowConfidence()
    {
        // Arrange
        var compiled = BuildSyntaxOnlyResult();
        await _store.CreateBaselineAsync(_repoId, _sha, compiled, _tempDir);

        var symbolId = SymbolId.From(BrokenSymbolId);

        // Act
        var result = await _engine.GetSymbolCardAsync(_routing, symbolId);

        // Assert: card returned with Low confidence; SemanticLevel == SyntaxOnly
        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Confidence.Should().Be(Confidence.Low);
        result.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.SyntaxOnly);
    }

    // ── Test 3: Broken build — refs.find works, meta shows SyntaxOnly ─────────

    [Fact]
    public async Task E2E_BrokenBuild_RefsFindReturnsUnresolved_MetaShowsSyntaxOnly()
    {
        // Arrange: index with unresolved refs from syntactic extraction
        var compiled = BuildSyntaxOnlyResultWithRefs();
        await _store.CreateBaselineAsync(_repoId, _sha, compiled, _tempDir);

        // refs.find on a known symbol (BrokenClass exists in symbols table)
        var symbolId = SymbolId.From(BrokenSymbolId);

        // Act: find refs TO BrokenClass (semantic API: who references this symbol)
        var result = await _engine.FindReferencesAsync(
            _routing, symbolId, kind: null, budgets: null);

        // Assert: query succeeds; SemanticLevel == SyntaxOnly (even with empty results)
        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.SyntaxOnly);

        // Verify unresolved edges exist in the store via outgoing refs query
        // (unresolved refs have from_symbol_id="BrokenClass::Process" and to_symbol_id="")
        var outgoing = await _store.GetOutgoingReferencesAsync(
            _repoId, _sha, SymbolId.From("BrokenClass::Process"), kind: null, limit: 50);
        outgoing.Should().NotBeEmpty("syntactic extractor should have produced outgoing edges");
        outgoing.Should().AllSatisfy(r => r.ResolutionState.Should().Be(ResolutionState.Unresolved));
        outgoing.Should().AllSatisfy(r => r.ToName.Should().NotBeNull());
    }

    // ── Test 4: Partial build — mixed confidence levels ───────────────────────

    [Fact]
    public async Task E2E_PartialBuild_WorkflowMixesConfidenceLevels()
    {
        // Arrange: partial compilation — one project compiled, one failed
        var compiled = BuildPartialResult();
        await _store.CreateBaselineAsync(_repoId, _sha, compiled, _tempDir);

        // Act: search for all symbols
        var searchResult = await _engine.SearchSymbolsAsync(
            _routing, "Class",
            new SymbolSearchFilters(),
            new BudgetLimits(maxResults: 10));

        // Assert: SemanticLevel == Partial; both symbols present
        searchResult.IsSuccess.Should().BeTrue();
        searchResult.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.Partial);
        searchResult.Value.Data.Hits.Should().HaveCount(2);

        // Use get_card to verify per-symbol confidence (search hits don't carry Confidence)
        var healthyCard = await _engine.GetSymbolCardAsync(_routing, SymbolId.From(HealthySymbolId));
        healthyCard.IsSuccess.Should().BeTrue();
        healthyCard.Value.Data.Confidence.Should().Be(Confidence.High);
        healthyCard.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.Partial);

        var brokenCard = await _engine.GetSymbolCardAsync(_routing, SymbolId.From(BrokenSymbolId));
        brokenCard.IsSuccess.Should().BeTrue();
        brokenCard.Value.Data.Confidence.Should().Be(Confidence.Low);
        brokenCard.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.Partial);
    }

    // ── Test 5: Workspace breaks compilation — graceful degradation ───────────

    [Fact]
    public async Task E2E_WorkspaceBreaksCompilation_GracefulDegradation()
    {
        // Arrange: full baseline (SemanticLevel.Full)
        var baselineResult = BuildFullResult();
        await _store.CreateBaselineAsync(_repoId, _sha, baselineResult, _tempDir);

        var wsDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(wsDir);

        var (workspaceId, mergedEngine, workspaceMgr, compiler) =
            BuildWorkspaceInfrastructure(wsDir);

        // Create the workspace
        await workspaceMgr.CreateWorkspaceAsync(
            _repoId, workspaceId, _sha, "/fake/solution.sln", _tempDir);

        // Mock compiler to return broken delta (SyntaxOnly)
        compiler.ComputeDeltaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OverlayDelta(
                ReindexedFiles: [],
                AddedOrUpdatedSymbols: [],
                DeletedSymbolIds: [],
                AddedOrUpdatedReferences: [],
                DeletedReferenceFiles: [],
                NewRevision: 1,
                SemanticLevel: SemanticLevel.SyntaxOnly)));

        // Pass an explicit file path so RefreshOverlayAsync doesn't exit early (no git changes)
        var refreshResult = await workspaceMgr.RefreshOverlayAsync(_repoId, workspaceId,
            [FilePath.From(FilePath2)]);
        refreshResult.IsSuccess.Should().BeTrue("overlay refresh must succeed");

        var wsRouting = new RoutingContext(
            repoId: _repoId,
            workspaceId: workspaceId,
            consistency: ConsistencyMode.Workspace,
            baselineCommitSha: _sha);

        // Act
        var result = await mergedEngine.SearchSymbolsAsync(
            wsRouting, "HealthyClass",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 10));

        // Assert: SemanticLevel degraded to SyntaxOnly (worst case: Full + SyntaxOnly = SyntaxOnly)
        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.SyntaxOnly);
    }

    // ── Test 6: Workspace fixes compilation — graceful upgrade ────────────────

    [Fact]
    public async Task E2E_WorkspaceFixesCompilation_GracefulUpgrade()
    {
        // Arrange: full baseline
        var baselineResult = BuildFullResult();
        await _store.CreateBaselineAsync(_repoId, _sha, baselineResult, _tempDir);

        var wsDir = Path.Combine(_tempDir, "overlays2");
        Directory.CreateDirectory(wsDir);

        var (workspaceId, mergedEngine, workspaceMgr, compiler) =
            BuildWorkspaceInfrastructure(wsDir);

        await workspaceMgr.CreateWorkspaceAsync(
            _repoId, workspaceId, _sha, "/fake/solution.sln", _tempDir);

        var wsRouting = new RoutingContext(
            repoId: _repoId,
            workspaceId: workspaceId,
            consistency: ConsistencyMode.Workspace,
            baselineCommitSha: _sha);

        // Pass explicit files so RefreshOverlayAsync doesn't exit early
        var changedFile = (IReadOnlyList<FilePath>)[FilePath.From(FilePath2)];

        // Step 1: Apply broken delta (SyntaxOnly)
        compiler.ComputeDeltaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OverlayDelta(
                ReindexedFiles: [],
                AddedOrUpdatedSymbols: [],
                DeletedSymbolIds: [],
                AddedOrUpdatedReferences: [],
                DeletedReferenceFiles: [],
                NewRevision: 1,
                SemanticLevel: SemanticLevel.SyntaxOnly)));

        await workspaceMgr.RefreshOverlayAsync(_repoId, workspaceId, changedFile);

        var degradedResult = await mergedEngine.SearchSymbolsAsync(
            wsRouting, "HealthyClass",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 10));
        degradedResult.IsSuccess.Should().BeTrue();
        degradedResult.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.SyntaxOnly,
            "should be degraded after broken overlay");

        // Step 2: Fix the overlay (Full compilation restored)
        compiler.ComputeDeltaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OverlayDelta(
                ReindexedFiles: [],
                AddedOrUpdatedSymbols: [],
                DeletedSymbolIds: [],
                AddedOrUpdatedReferences: [],
                DeletedReferenceFiles: [],
                NewRevision: 2,
                SemanticLevel: SemanticLevel.Full)));

        await workspaceMgr.RefreshOverlayAsync(_repoId, workspaceId, changedFile);

        // Clear cache so we don't get the stale result
        var result = await mergedEngine.SearchSymbolsAsync(
            wsRouting, "HealthyClass",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 10));

        // Assert: SemanticLevel restored to Full (baseline=Full, overlay=Full)
        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.Full,
            "should be restored after fixed overlay");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private (WorkspaceId wsId, MergedQueryEngine mergedEngine,
             WorkspaceManager wsMgr, IIncrementalCompiler compiler)
        BuildWorkspaceInfrastructure(string wsDir)
    {
        var overlayFactory = new OverlayDbFactory(wsDir, NullLogger<OverlayDbFactory>.Instance);
        var overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        var compiler = Substitute.For<IIncrementalCompiler>();
        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(
                Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var wsMgr = new WorkspaceManager(
            overlayStore, compiler, _store, git,
            new InMemoryCacheService(),
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        var mergedEngine = new MergedQueryEngine(
            _engine, overlayStore, wsMgr,
            new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(_store), new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);

        var wsId = WorkspaceId.From("degraded-ws-" + Guid.NewGuid().ToString("N")[..8]);
        return (wsId, mergedEngine, wsMgr, compiler);
    }

    private static CompilationResult BuildSyntaxOnlyResult()
    {
        var fileId = "brokf0001000000000";
        var file = new ExtractedFile(fileId, FilePath.From(FilePath1), new string('1', 64), "BrokenProject");

        var symbol = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(BrokenSymbolId),
            fullyQualifiedName: BrokenSymbolId,
            kind: SymbolKind.Class,
            signature: $"class {BrokenSymbolId}",
            @namespace: "",
            filePath: FilePath.From(FilePath1),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.Low);

        var diagnostics = (IReadOnlyList<ProjectDiagnostic>)[
            new ProjectDiagnostic(
                ProjectName:    "BrokenProject",
                Compiled:       false,
                SymbolCount:    1,
                ReferenceCount: 0,
                Errors:         ["Unexpected token '{'"])];

        return new CompilationResult(
            Symbols: [symbol],
            References: [],
            Files: [file],
            Stats: new IndexStats(
                SymbolCount: 1,
                ReferenceCount: 0,
                FileCount: 1,
                ElapsedSeconds: 0.1,
                Confidence: Confidence.Low,
                SemanticLevel: SemanticLevel.SyntaxOnly,
                ProjectDiagnostics: diagnostics));
    }

    private static CompilationResult BuildSyntaxOnlyResultWithRefs()
    {
        var fileId = "brokf0002000000000";
        var file = new ExtractedFile(fileId, FilePath.From(FilePath1), new string('2', 64), "BrokenProject");

        var symbol = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(BrokenSymbolId),
            fullyQualifiedName: BrokenSymbolId,
            kind: SymbolKind.Class,
            signature: $"class {BrokenSymbolId}",
            @namespace: "",
            filePath: FilePath.From(FilePath1),
            spanStart: 1,
            spanEnd: 20,
            visibility: "public",
            confidence: Confidence.Low);

        // Unresolved reference from syntactic extraction
        var unresolvedRef = new ExtractedReference(
            FromSymbol: SymbolId.From("BrokenClass::Process"),
            ToSymbol: SymbolId.Empty,
            Kind: RefKind.Call,
            FilePath: FilePath.From(FilePath1),
            LineStart: 5,
            LineEnd: 5,
            ResolutionState: ResolutionState.Unresolved,
            ToName: "Execute",
            ToContainerHint: "_service");

        var diagnostics = (IReadOnlyList<ProjectDiagnostic>)[
            new ProjectDiagnostic(
                ProjectName:    "BrokenProject",
                Compiled:       false,
                SymbolCount:    1,
                ReferenceCount: 1,
                Errors:         ["Unexpected token '{'"])];

        return new CompilationResult(
            Symbols: [symbol],
            References: [unresolvedRef],
            Files: [file],
            Stats: new IndexStats(
                SymbolCount: 1,
                ReferenceCount: 1,
                FileCount: 1,
                ElapsedSeconds: 0.1,
                Confidence: Confidence.Low,
                SemanticLevel: SemanticLevel.SyntaxOnly,
                ProjectDiagnostics: diagnostics));
    }

    private static CompilationResult BuildPartialResult()
    {
        var file1Id = "hlthy0001000000000";
        var file2Id = "brokf0003000000000";
        var file1 = new ExtractedFile(file1Id, FilePath.From(FilePath2), new string('3', 64), "HealthyProject");
        var file2 = new ExtractedFile(file2Id, FilePath.From(FilePath1), new string('4', 64), "BrokenProject");

        var healthySymbol = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(HealthySymbolId),
            fullyQualifiedName: HealthySymbolId,
            kind: SymbolKind.Class,
            signature: $"class {HealthySymbolId}",
            @namespace: "",
            filePath: FilePath.From(FilePath2),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High);

        var brokenSymbol = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(BrokenSymbolId),
            fullyQualifiedName: BrokenSymbolId,
            kind: SymbolKind.Class,
            signature: $"class {BrokenSymbolId}",
            @namespace: "",
            filePath: FilePath.From(FilePath1),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.Low);

        var diagnostics = (IReadOnlyList<ProjectDiagnostic>)[
            new ProjectDiagnostic("HealthyProject", Compiled: true,  SymbolCount: 1, ReferenceCount: 0),
            new ProjectDiagnostic("BrokenProject",  Compiled: false, SymbolCount: 1, ReferenceCount: 0,
                Errors: ["Missing reference"])];

        return new CompilationResult(
            Symbols: [healthySymbol, brokenSymbol],
            References: [],
            Files: [file1, file2],
            Stats: new IndexStats(
                SymbolCount: 2,
                ReferenceCount: 0,
                FileCount: 2,
                ElapsedSeconds: 0.2,
                Confidence: Confidence.Low,
                SemanticLevel: SemanticLevel.Partial,
                ProjectDiagnostics: diagnostics));
    }

    private static CompilationResult BuildFullResult()
    {
        var fileId = "hlthy0002000000000";
        var file = new ExtractedFile(fileId, FilePath.From(FilePath2), new string('5', 64), "HealthyProject");

        var symbol = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(HealthySymbolId),
            fullyQualifiedName: HealthySymbolId,
            kind: SymbolKind.Class,
            signature: $"class {HealthySymbolId}",
            @namespace: "",
            filePath: FilePath.From(FilePath2),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High);

        var diagnostics = (IReadOnlyList<ProjectDiagnostic>)[
            new ProjectDiagnostic("HealthyProject", Compiled: true, SymbolCount: 1, ReferenceCount: 0)];

        return new CompilationResult(
            Symbols: [symbol],
            References: [],
            Files: [file],
            Stats: new IndexStats(
                SymbolCount: 1,
                ReferenceCount: 0,
                FileCount: 1,
                ElapsedSeconds: 0.1,
                Confidence: Confidence.High,
                SemanticLevel: SemanticLevel.Full,
                ProjectDiagnostics: diagnostics));
    }
}
