namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Unit tests for MergedQueryEngine.GetSymbolByStableIdAsync (PHASE-03-01).
/// Verifies overlay-first merge: overlay hit → card, deleted overlay hit → NOT_FOUND,
/// no overlay hit → baseline fallback, committed mode → direct inner passthrough.
/// </summary>
public sealed class MergedQueryEngineStableIdTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-stable");
    private static readonly SymbolId SymId = SymbolId.From("T:OrderService");
    private static readonly StableId Stable = new("sym_" + new string('b', 16));

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly IQueryEngine _inner = Substitute.For<IQueryEngine>();
    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly WorkspaceManager _wsMgr;
    private readonly MergedQueryEngine _engine;

    public MergedQueryEngineStableIdTests()
    {
        // Register a workspace so workspace-mode routing resolves
        var baselineStore = Substitute.For<ISymbolStore>();
        baselineStore.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(true));
        var overlayForCreate = Substitute.For<IOverlayStore>();
        overlayForCreate.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
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

        // Default overlay: empty deleted set, no overlay files
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(null));
        _overlay.GetSymbolByStableIdAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<StableId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(null));

        // Cache miss by default
        _cache.GetAsync<ResponseEnvelope<SymbolCard>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolCard>?>(null));

        _engine = new MergedQueryEngine(
            _inner, _overlay, _wsMgr, _cache, _tracker,
            new ExcerptReader(Substitute.For<ISymbolStore>()),
            new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static RoutingContext WorkspaceRouting() =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private static SymbolCard MakeCard(SymbolId? id = null) =>
        SymbolCard.CreateMinimal(
            symbolId: id ?? SymId,
            fullyQualifiedName: (id ?? SymId).Value.TrimStart('T', ':'),
            kind: SymbolKind.Class,
            signature: "class OrderService",
            @namespace: "App",
            filePath: FilePath.From("src/OrderService.cs"),
            spanStart: 1,
            spanEnd: 20,
            visibility: "public",
            confidence: Confidence.High) with
        { StableId = Stable };

    private static ResponseEnvelope<SymbolCard> MakeCardEnvelope(SymbolCard card)
    {
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolCard>("answer", card, [], [], Confidence.High, meta);
    }

    // ── Tests: committed mode passthrough ─────────────────────────────────────

    [Fact]
    public async Task GetSymbolByStableId_CommittedMode_DelegatesToInner()
    {
        var envelope = MakeCardEnvelope(MakeCard());
        _inner.GetSymbolByStableIdAsync(Arg.Any<RoutingContext>(), Stable, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope));

        var result = await _engine.GetSymbolByStableIdAsync(CommittedRouting(), Stable);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetSymbolByStableIdAsync(Arg.Any<RoutingContext>(), Stable, Arg.Any<CancellationToken>());
    }

    // ── Tests: workspace mode — overlay hit ───────────────────────────────────

    [Fact]
    public async Task GetSymbolByStableId_WorkspaceMode_OverlayHit_ReturnsOverlayCard()
    {
        var overlayCard = MakeCard();
        _overlay.GetSymbolByStableIdAsync(Repo, WsId, Stable, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(overlayCard));

        // The merged engine calls GetSymbolCardAsync on itself (overlay-first) to build the envelope
        // _overlay.GetOverlaySymbolAsync is used internally by GetSymbolCardAsync in workspace mode
        _overlay.GetOverlaySymbolAsync(Repo, WsId, SymId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(overlayCard));
        _overlay.GetRevisionAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(1));
        _overlay.GetOverlaySemanticLevelAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SemanticLevel?>(SemanticLevel.Full));

        var result = await _engine.GetSymbolByStableIdAsync(WorkspaceRouting(), Stable);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.SymbolId.Should().Be(SymId);
    }

    [Fact]
    public async Task GetSymbolByStableId_WorkspaceMode_OverlayHitDeleted_ReturnsNotFound()
    {
        var overlayCard = MakeCard();
        _overlay.GetSymbolByStableIdAsync(Repo, WsId, Stable, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(overlayCard));

        // Mark the symbol as deleted in overlay
        _overlay.GetDeletedSymbolIdsAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { SymId }));

        var result = await _engine.GetSymbolByStableIdAsync(WorkspaceRouting(), Stable);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ── Tests: workspace mode — no overlay hit, baseline fallback ─────────────

    [Fact]
    public async Task GetSymbolByStableId_WorkspaceMode_NoOverlayHit_FallsBackToInner()
    {
        // overlay returns null (default), inner returns success
        var envelope = MakeCardEnvelope(MakeCard());
        _inner.GetSymbolByStableIdAsync(Arg.Any<RoutingContext>(), Stable, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope));

        var result = await _engine.GetSymbolByStableIdAsync(WorkspaceRouting(), Stable);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetSymbolByStableIdAsync(Arg.Any<RoutingContext>(), Stable, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSymbolByStableId_WorkspaceMode_NoOverlayHit_InnerNotFound_ReturnsNotFound()
    {
        _inner.GetSymbolByStableIdAsync(Arg.Any<RoutingContext>(), Stable, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                  CodeMapError.NotFound("Symbol", Stable.Value)));

        var result = await _engine.GetSymbolByStableIdAsync(WorkspaceRouting(), Stable);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetSymbolByStableId_WorkspaceMode_UnknownWorkspace_ReturnsNotFound()
    {
        var unknownWs = WorkspaceId.From("unknown-ws");
        var routing = new RoutingContext(
            repoId: Repo, workspaceId: unknownWs,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _engine.GetSymbolByStableIdAsync(routing, Stable);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }
}
