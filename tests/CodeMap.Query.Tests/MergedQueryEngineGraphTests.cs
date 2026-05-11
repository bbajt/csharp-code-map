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

public sealed class MergedQueryEngineGraphTests
{
    private static readonly RepoId Repo = RepoId.From("graph-test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('f', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-graph");
    private static readonly SymbolId Target = SymbolId.From("M:MyNs.Service.DoWork");
    private static readonly SymbolId Caller = SymbolId.From("M:MyNs.Controller.Act");
    private static readonly FilePath File1 = FilePath.From("src/Service.cs");

    private readonly IQueryEngine _inner = Substitute.For<IQueryEngine>();
    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly WorkspaceManager _wsMgr;
    private readonly MergedQueryEngine _engine;

    public MergedQueryEngineGraphTests()
    {
        // Build WorkspaceManager with workspace pre-registered
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

        // Default overlay state: empty (no deleted, no overlay files)
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));
        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredReference>>([]));
        _overlay.GetOutgoingOverlayReferencesAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredOutgoingReference>>([]));
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(),
                    Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(null));

        // Cache miss by default
        _cache.GetAsync<ResponseEnvelope<CallGraphResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<CallGraphResponse>?>(null));

        _engine = new MergedQueryEngine(
            _inner, _overlay, _wsMgr, _cache, _tracker,
            new ExcerptReader(Substitute.For<ISymbolStore>()),
            new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);
    }

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static RoutingContext WorkspaceRouting() =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private static ResponseEnvelope<CallGraphResponse> MakeGraphEnvelope(SymbolId root) =>
        new("Found 0 callers", new CallGraphResponse(root, [], 0, false), [], [],
            Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha, new Dictionary<string, LimitApplied>(), 0, 0));

    // ── Callers ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callers_CommittedMode_DelegatesToInner()
    {
        _inner.GetCallersAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                   Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Target))));

        var result = await _engine.GetCallersAsync(CommittedRouting(), Target, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetCallersAsync(
            Arg.Any<RoutingContext>(), Target,
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callers_WorkspaceMode_MergesOverlayAndBaseline()
    {
        // Overlay has a caller ref
        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), WsId, Target, null,
                     Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([new StoredReference(RefKind.Call, Caller, File1, 5, 5, null)]);
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), WsId, Caller, Arg.Any<CancellationToken>())
                .Returns(MakeCard(Caller));

        // Inner (baseline) FindReferencesAsync returns empty (no baseline callers)
        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Target, null,
                  Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(
                      new ResponseEnvelope<FindRefsResponse>(
                          "ok", new FindRefsResponse(Target, [], 0, false), [], [],
                          Confidence.High,
                          new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha, new Dictionary<string, LimitApplied>(), 0, 0)))));

        var result = await _engine.GetCallersAsync(WorkspaceRouting(), Target, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == Caller);
    }

    [Fact]
    public async Task Callers_WorkspaceMode_ExcludesDeletedCallers()
    {
        var deletedCaller = SymbolId.From("M:MyNs.DeletedController.Act");
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), WsId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(
                    new HashSet<SymbolId> { deletedCaller }));

        // Overlay returns deleted caller as a ref
        _overlay.GetOverlayReferencesAsync(Arg.Any<RepoId>(), WsId, Target, null,
                     Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([new StoredReference(RefKind.Call, deletedCaller, File1, 5, 5, null)]);

        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Target, null,
                  Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(
                      new ResponseEnvelope<FindRefsResponse>(
                          "ok", new FindRefsResponse(Target, [], 0, false), [], [],
                          Confidence.High,
                          new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha, new Dictionary<string, LimitApplied>(), 0, 0)))));

        var result = await _engine.GetCallersAsync(WorkspaceRouting(), Target, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Should().NotContain(n => n.SymbolId == deletedCaller);
    }

    // ── M20-03 interface-aware callers (workspace mode) ───────────────────────

    [Fact]
    public async Task Callers_CommittedMode_PropagatesFollowInterface()
    {
        // In committed mode MergedQueryEngine delegates straight to the inner engine.
        // The new follow_interface flag must round-trip unchanged.
        bool captured = false;
        _inner.GetCallersAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                   Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(),
                   Arg.Any<CancellationToken>(), Arg.Any<bool>())
              .Returns(ci =>
              {
                  captured = ci.ArgAt<bool>(6);
                  return Task.FromResult(
                      Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Target)));
              });

        await _engine.GetCallersAsync(
            CommittedRouting(), Target, 1, 20, null,
            ct: default, followInterface: true);

        captured.Should().BeTrue();
    }

    [Fact]
    public async Task Callers_WorkspaceMode_SurfacesHintFromInnerProbe()
    {
        // Inner GetCallersAsync (used as a baseline probe) returns a hint — the workspace
        // wrapper must re-emit it on the merged response.
        var ifaceMember = SymbolId.From("M:MyNs.IService.DoWork");
        var hint = new InterfaceImplementationHint([ifaceMember], 3, "pass follow_interface=true to include them");
        var envelopeWithHint = new ResponseEnvelope<CallGraphResponse>(
            "Found 0 callers",
            new CallGraphResponse(Target, [], 0, false, hint),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha, new Dictionary<string, LimitApplied>(), 0, 0));
        _inner.GetCallersAsync(Arg.Any<RoutingContext>(), Target,
                   Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(),
                   Arg.Any<CancellationToken>(), Arg.Any<bool>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(envelopeWithHint)));
        _inner.FindReferencesAsync(Arg.Any<RoutingContext>(), Target, null,
                  Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(
                      new ResponseEnvelope<FindRefsResponse>(
                          "ok", new FindRefsResponse(Target, [], 0, false), [], [],
                          Confidence.High,
                          new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha, new Dictionary<string, LimitApplied>(), 0, 0)))));

        var result = await _engine.GetCallersAsync(WorkspaceRouting(), Target, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.InterfaceImplementationHint.Should().NotBeNull();
        result.Value.Data.InterfaceImplementationHint!.Implements.Should().ContainSingle().Which.Should().Be(ifaceMember);
        result.Value.Data.InterfaceImplementationHint.AdditionalCallersViaInterface.Should().Be(3);
    }

    // ── Callees ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callees_CommittedMode_DelegatesToInner()
    {
        _inner.GetCalleesAsync(Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                   Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Target))));

        var result = await _engine.GetCalleesAsync(CommittedRouting(), Target, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetCalleesAsync(
            Arg.Any<RoutingContext>(), Target,
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callees_WorkspaceMode_MergesOverlayAndBaseline()
    {
        var callee = SymbolId.From("M:MyNs.Repo.Save");
        _overlay.GetOutgoingOverlayReferencesAsync(Arg.Any<RepoId>(), WsId, Target, null,
                     Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([new StoredOutgoingReference(RefKind.Call, callee, File1, 8, 8)]);
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), WsId, callee, Arg.Any<CancellationToken>())
                .Returns(MakeCard(callee));

        // Baseline callees returns empty
        _inner.GetCalleesAsync(Arg.Any<RoutingContext>(), Target,
                   Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Target))));

        var result = await _engine.GetCalleesAsync(WorkspaceRouting(), Target, 1, 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == callee);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(SymbolId id) =>
        SymbolCard.CreateMinimal(id, id.Value, SymbolKind.Method, "void Method()",
            "MyNs", File1, 5, 20, "public", Confidence.High);
}
