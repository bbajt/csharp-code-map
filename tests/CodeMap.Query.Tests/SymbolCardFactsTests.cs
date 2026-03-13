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
/// Tests that QueryEngine and MergedQueryEngine hydrate SymbolCard.Facts
/// from stored facts when returning symbol cards (PHASE-03-05).
/// </summary>
public class SymbolCardFactsTests
{
    private static readonly RepoId Repo = RepoId.From("facts-test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));
    private static readonly SymbolId SymId = SymbolId.From("M:App.Startup.Configure");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    // ─── QueryEngine (baseline mode) ─────────────────────────────────────────

    [Fact]
    public async Task GetCard_SymbolWithFacts_CardFactsPopulated()
    {
        var store = Substitute.For<ISymbolStore>();
        var tracker = Substitute.For<ITokenSavingsTracker>();
        var cache = new InMemoryCacheService();
        var engine = new QueryEngine(store, cache, tracker,
            new ExcerptReader(store), new GraphTraverser(), new FeatureTracer(store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        store.BaselineExistsAsync(Repo, Sha).Returns(true);
        store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());
        store.GetFactsForSymbolAsync(Repo, Sha, SymId)
             .Returns(Task.FromResult<IReadOnlyList<StoredFact>>([
                 MakeStoredFact(SymId, FactKind.DiRegistration, "IService \u2192 ServiceImpl|Scoped"),
             ]));

        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Facts.Should().HaveCount(1);
        result.Value.Data.Facts[0].Kind.Should().Be(FactKind.DiRegistration);
        result.Value.Data.Facts[0].Value.Should().Contain("IService");
    }

    [Fact]
    public async Task GetCard_SymbolNoFacts_CardFactsEmpty()
    {
        var store = Substitute.For<ISymbolStore>();
        var tracker = Substitute.For<ITokenSavingsTracker>();
        var cache = new InMemoryCacheService();
        var engine = new QueryEngine(store, cache, tracker,
            new ExcerptReader(store), new GraphTraverser(), new FeatureTracer(store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        store.BaselineExistsAsync(Repo, Sha).Returns(true);
        store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());
        store.GetFactsForSymbolAsync(Repo, Sha, SymId)
             .Returns(Task.FromResult<IReadOnlyList<StoredFact>>([]));

        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCard_MultipleFacts_AllPopulated()
    {
        var store = Substitute.For<ISymbolStore>();
        var tracker = Substitute.For<ITokenSavingsTracker>();
        var cache = new InMemoryCacheService();
        var engine = new QueryEngine(store, cache, tracker,
            new ExcerptReader(store), new GraphTraverser(), new FeatureTracer(store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        store.BaselineExistsAsync(Repo, Sha).Returns(true);
        store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());
        store.GetFactsForSymbolAsync(Repo, Sha, SymId)
             .Returns(Task.FromResult<IReadOnlyList<StoredFact>>([
                 MakeStoredFact(SymId, FactKind.DiRegistration, "IServiceA \u2192 A|Scoped"),
                 MakeStoredFact(SymId, FactKind.DiRegistration, "IServiceB \u2192 B|Singleton"),
             ]));

        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.Value.Data.Facts.Should().HaveCount(2);
    }

    // ─── MergedQueryEngine (workspace mode) ──────────────────────────────────

    [Fact]
    public async Task GetCard_WorkspaceMode_OverlayFactsTakePrecedence()
    {
        var inner = Substitute.For<IQueryEngine>();
        var overlay = Substitute.For<IOverlayStore>();
        var cache = new InMemoryCacheService();
        var tracker = Substitute.For<ITokenSavingsTracker>();
        var wsId = WorkspaceId.From("ws-facts-01");
        var wsMgr = BuildWorkspaceManagerWithWorkspace(overlay, wsId);

        var routing = new RoutingContext(
            repoId: Repo, workspaceId: wsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        overlay.GetDeletedSymbolIdsAsync(Repo, wsId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        overlay.GetOverlaySymbolAsync(Repo, wsId, SymId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<SymbolCard?>(null)); // not in overlay → use baseline
        overlay.GetOverlaySemanticLevelAsync(Repo, wsId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<SemanticLevel?>(SemanticLevel.Full));
        overlay.GetOverlayFactsForSymbolAsync(Repo, wsId, SymId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<StoredFact>>([
                   MakeStoredFact(SymId, FactKind.DiRegistration, "INew \u2192 NewImpl|Scoped"),
               ]));

        // Inner returns card with baseline facts
        inner.GetSymbolCardAsync(Arg.Is<RoutingContext>(r => r.Consistency == ConsistencyMode.Committed), SymId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(
                 Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                     MakeEnvelope(MakeCard(withFacts: [MakeStoredFact(SymId, FactKind.Route, "GET /orders")])))));

        var engine = new MergedQueryEngine(
            inner, overlay, wsMgr, cache, tracker,
            new ExcerptReader(Substitute.For<ISymbolStore>()),
            new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);

        var result = await engine.GetSymbolCardAsync(routing, SymId);

        result.IsSuccess.Should().BeTrue();
        // Overlay facts take precedence over baseline facts
        result.Value.Data.Facts.Should().HaveCount(1);
        result.Value.Data.Facts[0].Kind.Should().Be(FactKind.DiRegistration);
    }

    [Fact]
    public async Task GetCard_WorkspaceMode_BaselineFactsUsedWhenNoOverlayFacts()
    {
        var inner = Substitute.For<IQueryEngine>();
        var overlay = Substitute.For<IOverlayStore>();
        var cache = new InMemoryCacheService();
        var tracker = Substitute.For<ITokenSavingsTracker>();
        var wsId = WorkspaceId.From("ws-facts-02");
        var wsMgr = BuildWorkspaceManagerWithWorkspace(overlay, wsId);

        var routing = new RoutingContext(
            repoId: Repo, workspaceId: wsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        overlay.GetDeletedSymbolIdsAsync(Repo, wsId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlySet<SymbolId>>(new HashSet<SymbolId>()));
        overlay.GetOverlaySymbolAsync(Repo, wsId, SymId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<SymbolCard?>(null));
        overlay.GetOverlaySemanticLevelAsync(Repo, wsId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<SemanticLevel?>(SemanticLevel.Full));
        overlay.GetOverlayFactsForSymbolAsync(Repo, wsId, SymId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<StoredFact>>([])); // no overlay facts

        // Inner returns card with baseline Route fact
        inner.GetSymbolCardAsync(Arg.Is<RoutingContext>(r => r.Consistency == ConsistencyMode.Committed), SymId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(
                 Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                     MakeEnvelope(MakeCard(withFacts: [MakeStoredFact(SymId, FactKind.Route, "GET /orders")])))));

        var engine = new MergedQueryEngine(
            inner, overlay, wsMgr, cache, tracker,
            new ExcerptReader(Substitute.For<ISymbolStore>()),
            new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);

        var result = await engine.GetSymbolCardAsync(routing, SymId);

        result.IsSuccess.Should().BeTrue();
        // Baseline facts are used when no overlay facts
        result.Value.Data.Facts.Should().HaveCount(1);
        result.Value.Data.Facts[0].Kind.Should().Be(FactKind.Route);
    }

    // ─── Factories ───────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(IReadOnlyList<StoredFact>? withFacts = null)
    {
        var card = SymbolCard.CreateMinimal(
            symbolId: SymId,
            fullyQualifiedName: "App.Startup.Configure",
            kind: SymbolKind.Method,
            signature: "Configure(IServiceCollection)",
            @namespace: "App",
            filePath: FilePath.From("src/Startup.cs"),
            spanStart: 10,
            spanEnd: 20,
            visibility: "public",
            confidence: Confidence.High);

        if (withFacts?.Count > 0)
            return card with { Facts = withFacts.Select(f => new Fact(f.Kind, f.Value)).ToList() };
        return card;
    }

    private static StoredFact MakeStoredFact(SymbolId symbolId, FactKind kind, string value) =>
        new(SymbolId: symbolId,
            StableId: null,
            Kind: kind,
            Value: value,
            FilePath: FilePath.From("src/Startup.cs"),
            LineStart: 10,
            LineEnd: 10,
            Confidence: Confidence.High);

    private static ResponseEnvelope<SymbolCard> MakeEnvelope(SymbolCard card)
    {
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolCard>(
            Answer: "Symbol card",
            Data: card,
            Evidence: [],
            NextActions: [],
            Confidence: Confidence.High,
            Meta: meta);
    }

    private static WorkspaceManager BuildWorkspaceManagerWithWorkspace(
        IOverlayStore overlay, WorkspaceId wsId)
    {
        // Wire GetOverlayFilePathsAsync (used by CreateWorkspaceAsync)
        overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlySet<FilePath>>(new HashSet<FilePath>()));

        var store = Substitute.For<ISymbolStore>();
        store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(true));

        var compiler = Substitute.For<IIncrementalCompiler>();
        var git = Substitute.For<IGitService>();
        var cache = new InMemoryCacheService();
        var wsMgr = new WorkspaceManager(
            overlay, compiler, store, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        // Register workspace so GetWorkspaceInfo returns non-null
        wsMgr.CreateWorkspaceAsync(Repo, wsId, Sha, "/fake/solution.sln", "/fake/repo")
             .GetAwaiter().GetResult();

        return wsMgr;
    }
}
