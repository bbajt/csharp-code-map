namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using NSubstitute;

public sealed class FeatureTracerTests
{
    private static readonly RepoId Repo = RepoId.From("tracer-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));

    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly GraphTraverser _traverser = new();
    private readonly FeatureTracer _tracer;

    public FeatureTracerTests()
    {
        _tracer = new FeatureTracer(_store, _traverser);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(string id) =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(id),
            fullyQualifiedName: id,
            kind: SymbolKind.Method,
            signature: id,
            @namespace: "Ns",
            filePath: FilePath.From("A.cs"),
            spanStart: 1,
            spanEnd: 5,
            visibility: "public",
            confidence: Confidence.High);

    private void SetupCard(string id)
        => _store.GetSymbolAsync(Repo, Sha, SymbolId.From(id), Arg.Any<CancellationToken>())
               .Returns(MakeCard(id));

    private void SetupNoCard(string id)
        => _store.GetSymbolAsync(Repo, Sha, SymbolId.From(id), Arg.Any<CancellationToken>())
               .Returns((SymbolCard?)null);

    private void SetupCallees(string fromId, params string[] toIds)
        => _store.GetOutgoingReferencesAsync(Repo, Sha, SymbolId.From(fromId), null, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(toIds.Select(id => new StoredOutgoingReference(
                   Kind: RefKind.Call,
                   ToSymbol: SymbolId.From(id),
                   FilePath: FilePath.From("A.cs"),
                   LineStart: 1,
                   LineEnd: 1)).ToList<StoredOutgoingReference>());

    private void SetupNoCallees(string fromId)
        => _store.GetOutgoingReferencesAsync(Repo, Sha, SymbolId.From(fromId), null, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<StoredOutgoingReference>());

    private void SetupFacts(string symbolId, params (FactKind kind, string value)[] facts)
        => _store.GetFactsForSymbolAsync(Repo, Sha, SymbolId.From(symbolId), Arg.Any<CancellationToken>())
               .Returns(facts.Select(f => new StoredFact(
                   SymbolId: SymbolId.From(symbolId),
                   StableId: null,
                   Kind: f.kind,
                   Value: f.value,
                   FilePath: FilePath.From("A.cs"),
                   LineStart: 1,
                   LineEnd: 1,
                   Confidence: Confidence.High)).ToList<StoredFact>());

    private void SetupNoFacts(string symbolId)
        => _store.GetFactsForSymbolAsync(Repo, Sha, SymbolId.From(symbolId), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<StoredFact>());

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trace_SingleNode_NoCallees_ReturnsEntryOnly()
    {
        SetupCard("A");
        SetupNoCallees("A");
        SetupNoFacts("A");

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.EntryPoint.Value.Should().Be("A");
        result.Value.Nodes.Should().HaveCount(1);
        result.Value.Nodes[0].Children.Should().BeEmpty();
        result.Value.TotalNodesTraversed.Should().Be(1);
    }

    [Fact]
    public async Task Trace_LinearChain_BuildsCorrectTree()
    {
        // A → B → C
        SetupCard("A"); SetupCard("B"); SetupCard("C");
        SetupCallees("A", "B");
        SetupCallees("B", "C");
        SetupNoCallees("C");
        SetupNoFacts("A"); SetupNoFacts("B"); SetupNoFacts("C");

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var root = result.Value.Nodes[0];
        root.SymbolId.Value.Should().Be("A");
        root.Children.Should().HaveCount(1);

        var b = root.Children[0];
        b.SymbolId.Value.Should().Be("B");
        b.Children.Should().HaveCount(1);

        var c = b.Children[0];
        c.SymbolId.Value.Should().Be("C");
        c.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task Trace_BranchingCallees_AllBranchesIncluded()
    {
        // A → B, A → C, B → D
        SetupCard("A"); SetupCard("B"); SetupCard("C"); SetupCard("D");
        SetupCallees("A", "B", "C");
        SetupCallees("B", "D");
        SetupNoCallees("C"); SetupNoCallees("D");
        SetupNoFacts("A"); SetupNoFacts("B"); SetupNoFacts("C"); SetupNoFacts("D");

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var root = result.Value.Nodes[0];
        root.Children.Should().HaveCount(2);

        var bNode = root.Children.First(c => c.SymbolId.Value == "B");
        bNode.Children.Should().HaveCount(1);
        bNode.Children[0].SymbolId.Value.Should().Be("D");

        var cNode = root.Children.First(c => c.SymbolId.Value == "C");
        cNode.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task Trace_CyclicCalls_NoDuplicates()
    {
        // A → B → A (cycle)
        SetupCard("A"); SetupCard("B");
        SetupCallees("A", "B");
        SetupCallees("B", "A");
        SetupNoFacts("A"); SetupNoFacts("B");

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalNodesTraversed.Should().BeLessOrEqualTo(3, "cycle detection prevents infinite loop");
    }

    [Fact]
    public async Task Trace_DepthLimit_RespectsMaxDepth()
    {
        // Chain: A → B → C → D, depth=2 → A, B, C only (D excluded)
        SetupCard("A"); SetupCard("B"); SetupCard("C"); SetupCard("D");
        SetupCallees("A", "B");
        SetupCallees("B", "C");
        SetupCallees("C", "D");
        SetupNoCallees("D");
        SetupNoFacts("A"); SetupNoFacts("B"); SetupNoFacts("C"); SetupNoFacts("D");

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 2, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var root = result.Value.Nodes[0];
        var b = root.Children[0];
        var c = b.Children[0];
        c.Children.Should().BeEmpty("D is beyond max depth");
    }

    [Fact]
    public async Task Trace_NodeWithFacts_AnnotationsPopulated()
    {
        SetupCard("A"); SetupCard("B");
        SetupCallees("A", "B");
        SetupNoCallees("B");
        SetupNoFacts("A");
        SetupFacts("B", (FactKind.Route, "GET /api/orders"), (FactKind.Config, "App:Key|GetValue"));

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var b = result.Value.Nodes[0].Children[0];
        b.Annotations.Should().HaveCount(2);
        b.Annotations[0].Kind.Should().Be("Route");
        b.Annotations[0].Value.Should().Be("GET /api/orders");
        b.Annotations[1].Kind.Should().Be("Config");
        b.Annotations[1].Value.Should().Be("App:Key", "pipe-separated metadata stripped");
    }

    [Fact]
    public async Task Trace_EntryPointIsEndpoint_RouteAnnotated()
    {
        SetupCard("A");
        SetupNoCallees("A");
        SetupFacts("A", (FactKind.Route, "POST /api/orders"));

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.EntryPointRoute.Should().Be("POST /api/orders");
    }

    [Fact]
    public async Task Trace_EntryPointNotFound_ReturnsNotFound()
    {
        SetupNoCard("NonExistent");

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("NonExistent"), depth: 3, limit: 100);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(Core.Errors.ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Trace_ExternalCallee_DisplayNameFromSymbolId()
    {
        SetupCard("A");
        SetupCallees("A", "System.Console.WriteLine");
        SetupNoCard("System.Console.WriteLine");
        SetupNoFacts("A");
        _store.GetFactsForSymbolAsync(Repo, Sha, SymbolId.From("System.Console.WriteLine"), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredFact>());
        _store.GetOutgoingReferencesAsync(Repo, Sha, SymbolId.From("System.Console.WriteLine"), null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredOutgoingReference>());

        var result = await _tracer.TraceAsync(Repo, Sha, SymbolId.From("A"), depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var external = result.Value.Nodes[0].Children.FirstOrDefault(c => c.SymbolId.Value == "System.Console.WriteLine");
        external.Should().NotBeNull();
        external!.DisplayName.Should().Be("System.Console.WriteLine", "falls back to SymbolId when no card");
    }
}
