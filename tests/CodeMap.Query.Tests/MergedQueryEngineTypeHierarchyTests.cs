namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class MergedQueryEngineTypeHierarchyTests
{
    private static readonly RepoId Repo = RepoId.From("hier-merge-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-hier-01");
    private static readonly SymbolId Target = SymbolId.From("T:MyNs.MyClass");
    private static readonly SymbolId BaseId = SymbolId.From("T:MyNs.BaseClass");
    private static readonly SymbolId IfaceId = SymbolId.From("T:MyNs.IMyIface");
    private static readonly SymbolId DrvId = SymbolId.From("T:MyNs.DerivedClass");
    private static readonly FilePath File1 = FilePath.From("src/MyClass.cs");

    private readonly IQueryEngine _inner = Substitute.For<IQueryEngine>();
    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly WorkspaceManager _wsMgr;
    private readonly MergedQueryEngine _engine;

    public MergedQueryEngineTypeHierarchyTests()
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

        // Default overlay state: empty
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

        // Cache miss
        _cache.GetAsync<ResponseEnvelope<TypeHierarchyResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<ResponseEnvelope<TypeHierarchyResponse>?>(null));

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

    private static RoutingContext EphemeralRouting(IReadOnlyList<VirtualFile>? vf = null) =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Ephemeral,
            baselineCommitSha: Sha, virtualFiles: vf);

    private static ResponseEnvelope<TypeHierarchyResponse> MakeHierarchyEnvelope(
        TypeRef? baseType = null,
        IReadOnlyList<TypeRef>? ifaces = null,
        IReadOnlyList<TypeRef>? derived = null) =>
        new("Found hierarchy",
            new TypeHierarchyResponse(Target, baseType, ifaces ?? [], derived ?? []),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0));

    private static SymbolCard MakeCard(SymbolId id, SymbolKind kind = SymbolKind.Class, FilePath? file = null) =>
        SymbolCard.CreateMinimal(id, id.Value, kind, $"{kind} {id.Value}",
            "MyNs", file ?? File1, 1, 10, "public", Confidence.High);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hierarchy_CommittedMode_DelegatesToInner()
    {
        var envelope = MakeHierarchyEnvelope(new TypeRef(BaseId, "BaseClass"));
        _inner.GetTypeHierarchyAsync(Arg.Any<RoutingContext>(), Target, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(envelope));

        var result = await _engine.GetTypeHierarchyAsync(CommittedRouting(), Target);

        result.IsSuccess.Should().BeTrue();
        await _inner.Received(1).GetTypeHierarchyAsync(
            Arg.Any<RoutingContext>(), Target, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hierarchy_EphemeralMode_TreatedAsWorkspace()
    {
        // Ephemeral routing should normalize to workspace, which then delegates to inner for base data
        var envelope = MakeHierarchyEnvelope();
        _inner.GetTypeHierarchyAsync(Arg.Any<RoutingContext>(), Target, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(envelope));

        var result = await _engine.GetTypeHierarchyAsync(EphemeralRouting(), Target);

        // Should succeed and NOT just fail — ephemeral is treated as workspace
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Hierarchy_WorkspaceMode_DeletedType_ReturnsNotFound()
    {
        _overlay.GetDeletedSymbolIdsAsync(Arg.Any<RepoId>(), WsId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId> { Target }));

        var result = await _engine.GetTypeHierarchyAsync(WorkspaceRouting(), Target);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Hierarchy_WorkspaceMode_OverlayTypeRelationsUsed_WhenTypeInOverlayFiles()
    {
        // The type's file is in overlay files → use overlay relations
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), WsId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath> { File1 }));
        _overlay.GetOverlaySymbolAsync(Arg.Any<RepoId>(), WsId, Target, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<SymbolCard?>(MakeCard(Target)));
        _overlay.GetOverlayTypeRelationsAsync(Arg.Any<RepoId>(), WsId, Target, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredTypeRelation>>([
                    new StoredTypeRelation(Target, IfaceId, TypeRelationKind.Interface, "IMyIface")
                ]));

        // Baseline provides empty hierarchy
        var emptyEnvelope = MakeHierarchyEnvelope();
        _inner.GetTypeHierarchyAsync(Arg.Any<RoutingContext>(), Target, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(emptyEnvelope));

        var result = await _engine.GetTypeHierarchyAsync(WorkspaceRouting(), Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == IfaceId);
    }

    [Fact]
    public async Task Hierarchy_WorkspaceMode_BaselineRelationsUsed_WhenTypeNotInOverlay()
    {
        // Type's file is NOT in overlay → use baseline relations
        var baselineEnvelope = MakeHierarchyEnvelope(
            baseType: new TypeRef(BaseId, "BaseClass"),
            ifaces: [new TypeRef(IfaceId, "IMyIface")]);
        _inner.GetTypeHierarchyAsync(Arg.Any<RoutingContext>(), Target, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(baselineEnvelope));

        var result = await _engine.GetTypeHierarchyAsync(WorkspaceRouting(), Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.BaseType.Should().NotBeNull();
        result.Value.Data.BaseType!.SymbolId.Should().Be(BaseId);
        result.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == IfaceId);
    }

    [Fact]
    public async Task Hierarchy_WorkspaceMode_MergesDerivedFromBothStores()
    {
        var overlayDerived = SymbolId.From("T:MyNs.NewDerived");
        var baselineDerived = SymbolId.From("T:MyNs.OldDerived");

        // Baseline has one derived type
        var baselineEnvelope = MakeHierarchyEnvelope(
            derived: [new TypeRef(baselineDerived, "OldDerived")]);
        _inner.GetTypeHierarchyAsync(Arg.Any<RoutingContext>(), Target, Arg.Any<CancellationToken>())
              .Returns(Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(baselineEnvelope));

        // Overlay adds a new derived type
        _overlay.GetOverlayDerivedTypesAsync(Arg.Any<RepoId>(), WsId, Target, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<StoredTypeRelation>>([
                    new StoredTypeRelation(overlayDerived, Target, TypeRelationKind.BaseType, "NewDerived")
                ]));

        var result = await _engine.GetTypeHierarchyAsync(WorkspaceRouting(), Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.DerivedTypes.Should().Contain(r => r.SymbolId == overlayDerived);
        result.Value.Data.DerivedTypes.Should().Contain(r => r.SymbolId == baselineDerived);
    }
}
