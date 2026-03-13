namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Unit tests for MergedQueryEngine.FindReferencesAsync — workspace-mode ref merge.
/// </summary>
public sealed class MergedQueryEngineFindRefsTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-refs-test");
    private static readonly SymbolId Target = SymbolId.From("M:MyService.DoWork");
    private static readonly FilePath FileA = FilePath.From("src/CallerA.cs");
    private static readonly FilePath FileB = FilePath.From("src/CallerB.cs");

    private readonly IQueryEngine _inner = Substitute.For<IQueryEngine>();
    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly ICacheService _cache = new InMemoryCacheService();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly WorkspaceManager _wsMgr;
    private readonly MergedQueryEngine _engine;

    public MergedQueryEngineFindRefsTests()
    {
        // Build WorkspaceManager and pre-register workspace
        var overlayForCreate = Substitute.For<IOverlayStore>();
        overlayForCreate.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        var baselineStore = Substitute.For<ISymbolStore>();
        baselineStore.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
                     .Returns(true);

        _wsMgr = new WorkspaceManager(
            overlayForCreate,
            Substitute.For<IIncrementalCompiler>(),
            baselineStore,
            Substitute.For<IGitService>(),
            new InMemoryCacheService(),
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
        _wsMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", "/fake/repo")
              .GetAwaiter().GetResult();

        // Default overlay setup — empty overlay
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredReference>>([]));

        // Default inner returns empty refs for committed mode
        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                    Arg.Any<RefKind?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(ci => Task.FromResult(
                  Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(
                      MakeRefsEnvelope(Target, []))));

        _engine = new MergedQueryEngine(
            _inner, _overlay, _wsMgr, _cache, _tracker,
            new ExcerptReader(Substitute.For<ISymbolStore>()),
            new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindRefs_CommittedMode_DelegatesToInner()
    {
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        await _inner.Received(1).FindReferencesAsync(
            Arg.Any<RoutingContext>(), Target, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindRefs_WorkspaceMode_MergesBaselineAndOverlay()
    {
        var overlayRef = MakeStoredRef(SymbolId.From("M:Caller.OverlayMethod"), FileA, 5);
        var baselineRef = MakeClassifiedRef(SymbolId.From("M:Caller.BaselineMethod"), FileB, 10);

        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredReference>>([overlayRef]));

        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Target, null, Arg.Any<BudgetLimits?>(),
                    Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(
                      MakeRefsEnvelope(Target, [baselineRef]))));

        var routing = WorkspaceRouting();
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(2);
        // Overlay ref comes first
        result.Value.Data.References[0].FromSymbol.Value.Should().Contain("OverlayMethod");
        result.Value.Data.References[1].FromSymbol.Value.Should().Contain("BaselineMethod");
    }

    [Fact]
    public async Task FindRefs_WorkspaceMode_ExcludesRefsFromReindexedFiles()
    {
        // FileA is reindexed in the overlay — baseline refs from FileA should be excluded
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath> { FileA }));

        var baselineRefInFileA = MakeClassifiedRef(SymbolId.From("M:Old.Method"), FileA, 10);
        var baselineRefInFileB = MakeClassifiedRef(SymbolId.From("M:Keep.Method"), FileB, 20);

        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Target, null, Arg.Any<BudgetLimits?>(),
                    Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(
                      MakeRefsEnvelope(Target, [baselineRefInFileA, baselineRefInFileB]))));

        var routing = WorkspaceRouting();
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(1);
        result.Value.Data.References[0].FromSymbol.Value.Should().Contain("Keep");
    }

    [Fact]
    public async Task FindRefs_WorkspaceMode_DeletedSymbol_ReturnsNotFound()
    {
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { Target }));

        var routing = WorkspaceRouting();
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FindRefs_WorkspaceMode_OverlayRefsIncluded()
    {
        var overlayRef = MakeStoredRef(SymbolId.From("M:New.AddedMethod"), FileA, 3);
        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredReference>>([overlayRef]));

        var routing = WorkspaceRouting();
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().Contain(r => r.FromSymbol.Value.Contains("AddedMethod"));
    }

    [Fact]
    public async Task FindRefs_WorkspaceMode_CacheHit_ReturnsCached()
    {
        var routing = WorkspaceRouting();

        // First call — populates cache
        await _engine.FindReferencesAsync(routing, Target, null, null);

        // Second call — should hit cache, inner NOT called again
        await _engine.FindReferencesAsync(routing, Target, null, null);

        // Inner called once for baseline refs (from first call only)
        await _inner.Received(1).FindReferencesAsync(
            Arg.Any<RoutingContext>(), Target, null, Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindRefs_WorkspaceMode_WorkspaceNotFound_ReturnsError()
    {
        var unknownWs = WorkspaceId.From("ws-unknown");
        var routing = new RoutingContext(
            repoId: Repo,
            workspaceId: unknownWs,
            consistency: ConsistencyMode.Workspace,
            baselineCommitSha: Sha);

        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext WorkspaceRouting() =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private static StoredReference MakeStoredRef(SymbolId from, FilePath file, int line) =>
        new(RefKind.Call, from, file, line, line, null);

    private static ClassifiedReference MakeClassifiedRef(SymbolId from, FilePath file, int line) =>
        new(RefKind.Call, from, file, line, line, "// existing code");

    private static ResponseEnvelope<FindRefsResponse> MakeRefsEnvelope(
        SymbolId target, IReadOnlyList<ClassifiedReference> refs)
    {
        var data = new FindRefsResponse(target, refs, refs.Count, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<FindRefsResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
