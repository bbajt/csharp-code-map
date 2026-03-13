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
/// Unit tests for <see cref="MergedQueryEngine"/>.
/// Mocks the inner IQueryEngine, IOverlayStore, WorkspaceManager, and ICacheService.
/// </summary>
public sealed class MergedQueryEngineTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-test");
    private static readonly SymbolId SymId = SymbolId.From("T:OrderService");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly IQueryEngine _inner = Substitute.For<IQueryEngine>();
    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly WorkspaceManager _wsMgr;
    private readonly MergedQueryEngine _engine;

    private readonly WorkspaceInfo _wsInfo = new(
        WorkspaceId: WsId,
        RepoId: Repo,
        BaselineCommitSha: Sha,
        CurrentRevision: 1,
        SolutionPath: "/fake/solution.sln",
        RepoRootPath: "/fake/repo",
        CreatedAt: DateTimeOffset.UtcNow);

    public MergedQueryEngineTests()
    {
        // Build a WorkspaceManager with all mocked deps
        var overlayMgr = Substitute.For<IOverlayStore>();
        overlayMgr.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));

        _wsMgr = new WorkspaceManager(
            overlayMgr,
            Substitute.For<IIncrementalCompiler>(),
            Substitute.For<ISymbolStore>(),
            Substitute.For<IGitService>(),
            new InMemoryCacheService(),
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        // Pre-register workspace in the manager's registry
        var baselineStore = Substitute.For<ISymbolStore>();
        baselineStore.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(true));
        var overlayForCreate = Substitute.For<IOverlayStore>();
        overlayForCreate.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        var wsMgr2 = new WorkspaceManager(
            overlayForCreate,
            Substitute.For<IIncrementalCompiler>(),
            baselineStore,
            Substitute.For<IGitService>(),
            new InMemoryCacheService(),
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
        wsMgr2.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", "/fake/repo")
              .GetAwaiter().GetResult();
        _wsMgr = wsMgr2;

        // Default overlay setup
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        _overlay.SearchOverlaySymbolsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<string>(), Arg.Any<SymbolSearchFilters?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<SymbolSearchHit>>([]));
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(null));

        // Cache miss by default
        _cache.GetAsync<ResponseEnvelope<SymbolSearchResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolSearchResponse>?>(null));
        _cache.GetAsync<ResponseEnvelope<SymbolCard>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolCard>?>(null));
        _cache.GetAsync<ResponseEnvelope<SpanResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SpanResponse>?>(null));

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

    private static SymbolSearchHit MakeHit(string id, string file = "src/A.cs") =>
        new(SymbolId.From(id), id, SymbolKind.Class, $"class {id}", null, FilePath.From(file), 1, 1.0);

    private static ResponseEnvelope<SymbolSearchResponse> MakeSearchEnvelope(params SymbolSearchHit[] hits)
    {
        var data = new SymbolSearchResponse(hits, hits.Length, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolSearchResponse>("answer", data, [], [], Confidence.High, meta);
    }

    private static ResponseEnvelope<SymbolCard> MakeCardEnvelope(SymbolCard card)
    {
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolCard>("answer", card, [], [], Confidence.High, meta);
    }

    private static ResponseEnvelope<SpanResponse> MakeSpanEnvelope()
    {
        var span = new SpanResponse(FilePath.From("src/A.cs"), 1, 10, 40, "// code", false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SpanResponse>("answer", span, [], [], Confidence.High, meta);
    }

    private static SymbolCard MakeCard(string id = "T:OrderService") =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(id),
            fullyQualifiedName: id.TrimStart('T', ':'),
            kind: SymbolKind.Class,
            signature: $"class {id.TrimStart('T', ':')}",
            @namespace: "Test",
            filePath: FilePath.From("src/A.cs"),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High);

    // ── Committed mode — passthrough ──────────────────────────────────────────

    [Fact]
    public async Task Search_CommittedMode_DelegatesToInnerEngine()
    {
        var expected = MakeSearchEnvelope(MakeHit("T:Foo"));
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(expected)));

        var result = await _engine.SearchSymbolsAsync(CommittedRouting(), "Foo", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
        await _inner.Received(1).SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
        await _overlay.DidNotReceive().GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_CommittedMode_DelegatesToInnerEngine()
    {
        var expected = MakeCardEnvelope(MakeCard());
        _inner.GetSymbolCardAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(expected)));

        var result = await _engine.GetSymbolCardAsync(CommittedRouting(), SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Fact]
    public async Task GetSpan_CommittedMode_DelegatesToInnerEngine()
    {
        var expected = MakeSpanEnvelope();
        _inner.GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(), Arg.Any<int>(), Arg.Any<int>(),
                   Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(expected)));

        var result = await _engine.GetSpanAsync(CommittedRouting(), FilePath.From("src/A.cs"), 1, 10, 0, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetDefinitionSpan_CommittedMode_DelegatesToInnerEngine()
    {
        var expected = MakeSpanEnvelope();
        _inner.GetDefinitionSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                   Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(expected)));

        var result = await _engine.GetDefinitionSpanAsync(CommittedRouting(), SymId, 120, 2);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    // ── Workspace mode — search merge ─────────────────────────────────────────

    [Fact]
    public async Task Search_WorkspaceMode_MergesBaselineAndOverlay()
    {
        var baselineHit = MakeHit("T:Baseline", "src/B.cs");
        var overlayHit = MakeHit("T:Overlay", "src/O.cs");
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(baselineHit))));
        _overlay.SearchOverlaySymbolsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<string>(), Arg.Any<SymbolSearchFilters?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<SymbolSearchHit>>([overlayHit]));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "order", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == "T:Baseline");
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == "T:Overlay");
    }

    [Fact]
    public async Task Search_WorkspaceMode_ExcludesDeletedSymbols()
    {
        var deletedId = SymbolId.From("T:Deleted");
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(MakeHit("T:Deleted", "src/A.cs"), MakeHit("T:Kept", "src/B.cs")))));
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { deletedId }));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "order", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotContain(h => h.SymbolId == deletedId);
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == "T:Kept");
    }

    [Fact]
    public async Task Search_WorkspaceMode_OverlayReplacesBaselineByFile()
    {
        var reindexedFile = FilePath.From("src/A.cs");
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(MakeHit("T:OldFoo", "src/A.cs")))));
        _overlay.SearchOverlaySymbolsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<string>(), Arg.Any<SymbolSearchFilters?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<SymbolSearchHit>>([MakeHit("T:NewFoo", "src/A.cs")]));
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath> { reindexedFile }));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "foo", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotContain(h => h.SymbolId.Value == "T:OldFoo");
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId.Value == "T:NewFoo");
    }

    [Fact]
    public async Task Search_WorkspaceMode_OverlaySymbolsAppearFirst()
    {
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(MakeHit("T:Baseline", "src/B.cs")))));
        _overlay.SearchOverlaySymbolsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<string>(), Arg.Any<SymbolSearchFilters?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<SymbolSearchHit>>([MakeHit("T:Overlay", "src/O.cs")]));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "order", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits[0].SymbolId.Value.Should().Be("T:Overlay");
    }

    [Fact]
    public async Task Search_WorkspaceMode_RespectsLimit()
    {
        var baselineHits = Enumerable.Range(0, 10).Select(i => MakeHit($"T:B{i}", "src/B.cs")).ToArray();
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(baselineHits))));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "order", null,
            new BudgetLimits(maxResults: 5));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().HaveCount(5);
    }

    [Fact]
    public async Task Search_WorkspaceMode_NoOverlay_ReturnsBaselineOnly()
    {
        var hit = MakeHit("T:Foo", "src/B.cs");
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(hit))));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "foo", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().HaveCount(1);
        result.Value.Data.Hits[0].SymbolId.Value.Should().Be("T:Foo");
    }

    [Fact]
    public async Task Search_WorkspaceMode_AllDeleted_ReturnsEmpty()
    {
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope(MakeHit("T:Foo", "src/A.cs")))));
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { SymbolId.From("T:Foo") }));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "foo", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_WorkspaceMode_CacheHit_ReturnsCached()
    {
        var cached = MakeSearchEnvelope(MakeHit("T:Cached"));
        _cache.GetAsync<ResponseEnvelope<SymbolSearchResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolSearchResponse>?>(cached));

        var result = await _engine.SearchSymbolsAsync(WorkspaceRouting(), "foo", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(cached);
        await _inner.DidNotReceive().SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_WorkspaceMode_CacheMiss_QueriesBothStores()
    {
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                   Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                  .Success(MakeSearchEnvelope())));

        await _engine.SearchSymbolsAsync(WorkspaceRouting(), "foo", null, null);

        await _inner.Received(1).SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
        await _overlay.Received(1).SearchOverlaySymbolsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
            Arg.Any<string>(), Arg.Any<SymbolSearchFilters?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_WorkspaceMode_WorkspaceNotFound_ReturnsError()
    {
        var unknownWs = WorkspaceId.From("unknown-ws");
        var routing = new RoutingContext(
            repoId: Repo, workspaceId: unknownWs,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _engine.SearchSymbolsAsync(routing, "foo", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ── Workspace mode — get card merge ───────────────────────────────────────

    [Fact]
    public async Task GetCard_WorkspaceMode_OverlayExists_ReturnsOverlayVersion()
    {
        var overlayCard = MakeCard("T:Modified");
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(overlayCard));

        var result = await _engine.GetSymbolCardAsync(WorkspaceRouting(), SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Should().Be(overlayCard);
        await _inner.DidNotReceive().GetSymbolCardAsync(Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_WorkspaceMode_NoOverlay_ReturnsBaseline()
    {
        var baselineCard = MakeCard();
        _inner.GetSymbolCardAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolCard>, CodeMapError>
                  .Success(MakeCardEnvelope(baselineCard))));

        var result = await _engine.GetSymbolCardAsync(WorkspaceRouting(), SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Should().Be(baselineCard);
    }

    [Fact]
    public async Task GetCard_WorkspaceMode_SymbolDeleted_ReturnsNotFound()
    {
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { SymId }));

        var result = await _engine.GetSymbolCardAsync(WorkspaceRouting(), SymId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetCard_WorkspaceMode_CacheHit_ReturnsCached()
    {
        var cached = MakeCardEnvelope(MakeCard());
        _cache.GetAsync<ResponseEnvelope<SymbolCard>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolCard>?>(cached));

        var result = await _engine.GetSymbolCardAsync(WorkspaceRouting(), SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(cached);
        await _overlay.DidNotReceive().GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>());
    }

    // ── Workspace mode — get span ─────────────────────────────────────────────

    [Fact]
    public async Task GetSpan_WorkspaceMode_DelegatesToInner_CommittedRouting()
    {
        var expected = MakeSpanEnvelope();
        RoutingContext? capturedRouting = null;
        _inner.GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(), Arg.Any<int>(), Arg.Any<int>(),
                   Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(ci =>
              {
                  capturedRouting = ci.ArgAt<RoutingContext>(0);
                  return Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(expected));
              });

        var result = await _engine.GetSpanAsync(WorkspaceRouting(), FilePath.From("src/A.cs"), 1, 10, 0, null);

        result.IsSuccess.Should().BeTrue();
        capturedRouting!.Consistency.Should().Be(ConsistencyMode.Committed);
        capturedRouting.WorkspaceId.Should().BeNull();
    }

    // ── Workspace mode — get definition span ──────────────────────────────────

    [Fact]
    public async Task GetDefinitionSpan_WorkspaceMode_OverlaySymbol_UsesOverlaySpan()
    {
        var overlayCard = MakeCard();
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(overlayCard));
        _inner.GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(), Arg.Any<int>(), Arg.Any<int>(),
                   Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(MakeSpanEnvelope())));

        var result = await _engine.GetDefinitionSpanAsync(WorkspaceRouting(), SymId, 120, 2);

        result.IsSuccess.Should().BeTrue();
        // Overlay card was used (inner GetSymbolCardAsync NOT called)
        await _inner.DidNotReceive().GetSymbolCardAsync(Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDefinitionSpan_WorkspaceMode_BaselineSymbol_UsesBaselineSpan()
    {
        var baselineCard = MakeCard();
        _inner.GetSymbolCardAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolCard>, CodeMapError>
                  .Success(MakeCardEnvelope(baselineCard))));
        _inner.GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(), Arg.Any<int>(), Arg.Any<int>(),
                   Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(MakeSpanEnvelope())));

        var result = await _engine.GetDefinitionSpanAsync(WorkspaceRouting(), SymId, 120, 2);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetSymbolCardAsync(Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDefinitionSpan_WorkspaceMode_DeletedSymbol_ReturnsNotFound()
    {
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { SymId }));

        var result = await _engine.GetDefinitionSpanAsync(WorkspaceRouting(), SymId, 120, 2);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetDefinitionSpan_WorkspaceMode_CacheHit_ReturnsCached()
    {
        var cached = MakeSpanEnvelope();
        _cache.GetAsync<ResponseEnvelope<SpanResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SpanResponse>?>(cached));

        var result = await _engine.GetDefinitionSpanAsync(WorkspaceRouting(), SymId, 120, 2);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(cached);
        await _overlay.DidNotReceive().GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(),
            Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>());
    }
}
