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
/// Tests MergedQueryEngine's handling of ConsistencyMode.Ephemeral.
/// Span queries substitute virtual file content; semantic queries delegate as workspace.
/// </summary>
public sealed class EphemeralModeTests
{
    private static readonly RepoId Repo = RepoId.From("ephemeral-test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('7', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-ephemeral-01");
    private static readonly FilePath File1 = FilePath.From("src/MyClass.cs");
    private static readonly FilePath File2 = FilePath.From("src/Other.cs");
    private static readonly SymbolId Sym1 = SymbolId.From("T:MyNs.MyClass");

    private readonly IQueryEngine _inner = Substitute.For<IQueryEngine>();
    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly WorkspaceManager _wsMgr;
    private readonly MergedQueryEngine _engine;

    public EphemeralModeTests()
    {
        var baselineStore = Substitute.For<ISymbolStore>();
        baselineStore.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
                     .Returns(true);
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

        // Default overlay: empty
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(null));
        _overlay.GetOverlayTypeRelationsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]));
        _overlay.GetOverlayDerivedTypesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]));
        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredReference>>([]));
        _overlay.GetOutgoingOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredOutgoingReference>>([]));

        // Cache miss
        _cache.GetAsync<ResponseEnvelope<SpanResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SpanResponse>?>(null));
        _cache.GetAsync<ResponseEnvelope<SymbolSearchResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolSearchResponse>?>(null));
        _cache.GetAsync<ResponseEnvelope<SymbolCard>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<SymbolCard>?>(null));
        _cache.GetAsync<ResponseEnvelope<FindRefsResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<FindRefsResponse>?>(null));
        _cache.GetAsync<ResponseEnvelope<CallGraphResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<CallGraphResponse>?>(null));
        _cache.GetAsync<ResponseEnvelope<TypeHierarchyResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<TypeHierarchyResponse>?>(null));

        _engine = new MergedQueryEngine(
            _inner, _overlay, _wsMgr, _cache, _tracker,
            new ExcerptReader(Substitute.For<ISymbolStore>()),
            new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext EphemeralRouting(IReadOnlyList<VirtualFile>? vf = null) =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Ephemeral,
            baselineCommitSha: Sha, virtualFiles: vf);

    private static VirtualFile MakeVf(FilePath path, string content) => new(path, content);

    private static SymbolCard MakeCard(SymbolId id, FilePath? file = null, int start = 1, int end = 10) =>
        SymbolCard.CreateMinimal(id, id.Value, SymbolKind.Class, "class MyClass",
            "MyNs", file ?? File1, start, end, "public", Confidence.High);

    private static ResponseEnvelope<SpanResponse> MakeSpanEnvelope(FilePath fp, int s, int e, string content) =>
        new($"Span {s}-{e}",
            new SpanResponse(fp, s, e, 100, content, false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    private static ResponseEnvelope<SymbolSearchResponse> MakeSearchEnvelope() =>
        new("Found 0 results",
            new SymbolSearchResponse([], 0, false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    private static ResponseEnvelope<SymbolCard> MakeCardEnvelope(SymbolId id) =>
        new("Card",
            MakeCard(id),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    private static ResponseEnvelope<FindRefsResponse> MakeFindRefsEnvelope(SymbolId id) =>
        new("0 refs",
            new FindRefsResponse(id, [], 0, false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    private static ResponseEnvelope<CallGraphResponse> MakeCallGraphEnvelope(SymbolId id) =>
        new("0 callers",
            new CallGraphResponse(id, [], 0, false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    private static ResponseEnvelope<TypeHierarchyResponse> MakeHierarchyEnvelope(SymbolId id) =>
        new("Hierarchy",
            new TypeHierarchyResponse(id, null, [], []),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    // ── GetSpan tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSpan_Ephemeral_VirtualFileExists_ReturnsVirtualContent()
    {
        var content = "// virtual\nline2\nline3\nline4\nline5";
        var vf = new List<VirtualFile> { MakeVf(File1, content) };
        var routing = EphemeralRouting(vf);

        var result = await _engine.GetSpanAsync(routing, File1, 1, 3, 0, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("// virtual");
        // Inner should NOT be called — content came from virtual file
        await _inner.DidNotReceive().GetSpanAsync(
            Arg.Any<RoutingContext>(), File1, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSpan_Ephemeral_FileNotVirtual_ReadFromDisk()
    {
        // Virtual files only have File2, not File1
        var vf = new List<VirtualFile> { MakeVf(File2, "other content") };
        var routing = EphemeralRouting(vf);

        _inner.GetSpanAsync(Arg.Any<RoutingContext>(), File1, Arg.Any<int>(), Arg.Any<int>(),
                  Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(
                  MakeSpanEnvelope(File1, 1, 5, "disk content")));

        var result = await _engine.GetSpanAsync(routing, File1, 1, 5, 0, null);

        result.IsSuccess.Should().BeTrue();
        // Falls back to disk via inner engine
        await _inner.Received(1).GetSpanAsync(
            Arg.Any<RoutingContext>(), File1, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSpan_Ephemeral_NoVirtualFiles_BehavesAsWorkspace()
    {
        // Ephemeral with no virtual files falls back to workspace disk read
        var routing = EphemeralRouting(vf: null);

        _inner.GetSpanAsync(Arg.Any<RoutingContext>(), File1, Arg.Any<int>(), Arg.Any<int>(),
                  Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(
                  MakeSpanEnvelope(File1, 1, 5, "disk content")));

        var result = await _engine.GetSpanAsync(routing, File1, 1, 5, 0, null);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetSpanAsync(
            Arg.Any<RoutingContext>(), File1, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDefinitionSpan_Ephemeral_VirtualFileForSymbol_ReturnsVirtualContent()
    {
        var content = "// virtual\nclass MyClass {}";
        var vf = new List<VirtualFile> { MakeVf(File1, content) };
        var routing = EphemeralRouting(vf);

        // Overlay has symbol card
        _overlay.GetOverlaySymbolAsync(Repo, WsId, Sym1, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(MakeCard(Sym1, File1, 1, 2)));

        var result = await _engine.GetDefinitionSpanAsync(routing, Sym1, 120, 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("// virtual");
    }

    // ── Semantic query tests — all treated as workspace ────────────────────────

    [Fact]
    public async Task Search_Ephemeral_TreatedAsWorkspace()
    {
        _inner.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(), Arg.Any<SymbolSearchFilters?>(),
                  Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(MakeSearchEnvelope()));

        var result = await _engine.SearchSymbolsAsync(EphemeralRouting(), "MyClass", null, null);

        // Normalized to workspace → inner is called in committed path
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetCard_Ephemeral_TreatedAsWorkspace()
    {
        _inner.GetSymbolCardAsync(Arg.Any<RoutingContext>(), Sym1, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(MakeCardEnvelope(Sym1)));

        var result = await _engine.GetSymbolCardAsync(EphemeralRouting(), Sym1);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task FindRefs_Ephemeral_TreatedAsWorkspace()
    {
        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Sym1, Arg.Any<RefKind?>(),
                  Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(MakeFindRefsEnvelope(Sym1)));

        var result = await _engine.FindReferencesAsync(EphemeralRouting(), Sym1, null, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetCallers_Ephemeral_TreatedAsWorkspace()
    {
        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Sym1, Arg.Any<RefKind?>(),
                  Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(MakeFindRefsEnvelope(Sym1)));

        var result = await _engine.GetCallersAsync(EphemeralRouting(), Sym1, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Hierarchy_Ephemeral_TreatedAsWorkspace()
    {
        _inner.GetTypeHierarchyAsync(Arg.Any<RoutingContext>(), Sym1, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(
                  MakeHierarchyEnvelope(Sym1)));

        var result = await _engine.GetTypeHierarchyAsync(EphemeralRouting(), Sym1);

        result.IsSuccess.Should().BeTrue();
    }
}
