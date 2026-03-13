namespace CodeMap.Query.Tests;

using CodeMap.Core.Types;
using FluentAssertions;

public sealed class GraphTraverserTests
{
    private readonly GraphTraverser _traverser = new();

    private static SymbolId Sym(string name) => SymbolId.From($"M:Test.{name}");

    // ── Basic traversal ──────────────────────────────────────────────────────

    [Fact]
    public async Task Traverse_SingleLevel_ReturnsDirectConnections()
    {
        // A → B, C
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B"), Sym("C")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 1, limitPerLevel: 10);

        result.TotalNodesFound.Should().Be(2);
        result.Nodes.Select(n => n.SymbolId).Should().Contain([Sym("B"), Sym("C")]);
    }

    [Fact]
    public async Task Traverse_TwoLevels_ReturnsTransitiveConnections()
    {
        // A → B → D
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B")],
            [Sym("B")] = [Sym("D")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 2, limitPerLevel: 10);

        result.Nodes.Select(n => n.SymbolId).Should().Contain([Sym("B"), Sym("D")]);
        result.Nodes.First(n => n.SymbolId == Sym("D")).Depth.Should().Be(2);
    }

    [Fact]
    public async Task Traverse_MaxDepth_StopsAtBoundary()
    {
        // A → B → C → D  (depth 3 chain)
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B")],
            [Sym("B")] = [Sym("C")],
            [Sym("C")] = [Sym("D")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 2, limitPerLevel: 10);

        result.Nodes.Select(n => n.SymbolId).Should().NotContain(Sym("D"));
        result.TotalNodesFound.Should().Be(2); // B, C
    }

    [Fact]
    public async Task Traverse_EmptyGraph_ReturnsRootOnly()
    {
        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand([]), maxDepth: 3, limitPerLevel: 10);

        result.TotalNodesFound.Should().Be(0);
        result.Nodes.Should().ContainSingle(n => n.SymbolId == Sym("A"));
        result.Truncated.Should().BeFalse();
    }

    // ── Cycle detection ──────────────────────────────────────────────────────

    [Fact]
    public async Task Traverse_DirectCycle_AB_BA_NoDuplicates()
    {
        // A → B, B → A
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B")],
            [Sym("B")] = [Sym("A")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 5, limitPerLevel: 10);

        var nodeIds = result.Nodes.Select(n => n.SymbolId).ToList();
        nodeIds.Should().Contain(Sym("A"));
        nodeIds.Should().Contain(Sym("B"));
        nodeIds.Distinct().Should().HaveCount(nodeIds.Count); // no duplicates
    }

    [Fact]
    public async Task Traverse_SelfLoop_Handled()
    {
        // A → A
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("A")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 3, limitPerLevel: 10);

        result.TotalNodesFound.Should().Be(0); // self-loop — no new nodes found
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Traverse_DiamondPattern_VisitsOnce()
    {
        // A → B, A → C; B → D, C → D
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B"), Sym("C")],
            [Sym("B")] = [Sym("D")],
            [Sym("C")] = [Sym("D")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 3, limitPerLevel: 10);

        var nodeIds = result.Nodes.Select(n => n.SymbolId).ToList();
        nodeIds.Count(id => id == Sym("D")).Should().Be(1); // visited only once
    }

    // ── Limits ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Traverse_LimitPerLevel_TruncatesLevel()
    {
        // A → B, C, D, E (4 direct connections)
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B"), Sym("C"), Sym("D"), Sym("E")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 1, limitPerLevel: 2);

        result.TotalNodesFound.Should().Be(2);
    }

    [Fact]
    public async Task Traverse_LimitPerLevel_SetsTruncatedFlag()
    {
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B"), Sym("C"), Sym("D")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 1, limitPerLevel: 2);

        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Traverse_ZeroDepth_ReturnsRootOnly()
    {
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B")],
        };

        // maxDepth = 0 means no expansion
        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 0, limitPerLevel: 10);

        result.TotalNodesFound.Should().Be(0);
        result.Nodes.Should().ContainSingle(n => n.SymbolId == Sym("A"));
    }

    // ── Edge recording ───────────────────────────────────────────────────────

    [Fact]
    public async Task Traverse_EdgesRecorded_ForParentNodes()
    {
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B"), Sym("C")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 1, limitPerLevel: 10);

        var rootNode = result.Nodes.First(n => n.SymbolId == Sym("A"));
        rootNode.ConnectedIds.Should().Contain([Sym("B"), Sym("C")]);
    }

    [Fact]
    public async Task Traverse_LeafNodes_HaveEmptyEdges()
    {
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B")],
            // B has no outgoing connections
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 1, limitPerLevel: 10);

        var leafNode = result.Nodes.First(n => n.SymbolId == Sym("B"));
        leafNode.ConnectedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Traverse_CycleEdge_RecordedButNotExpanded()
    {
        // A → B → A (cycle back to root)
        var graph = new Dictionary<SymbolId, List<SymbolId>>
        {
            [Sym("A")] = [Sym("B")],
            [Sym("B")] = [Sym("A")],
        };

        var result = await _traverser.TraverseAsync(
            Sym("A"), Expand(graph), maxDepth: 3, limitPerLevel: 10);

        // B should record edge to A even though A was already visited
        var bNode = result.Nodes.FirstOrDefault(n => n.SymbolId == Sym("B"));
        bNode.Should().NotBeNull();
        bNode!.ConnectedIds.Should().Contain(Sym("A"));

        // But A should only appear once in the node list
        result.Nodes.Count(n => n.SymbolId == Sym("A")).Should().Be(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Func<SymbolId, CancellationToken, Task<IReadOnlyList<SymbolId>>> Expand(
        Dictionary<SymbolId, List<SymbolId>> graph) =>
        (sid, _) => Task.FromResult<IReadOnlyList<SymbolId>>(
            graph.TryGetValue(sid, out var conns) ? conns : []);
}
