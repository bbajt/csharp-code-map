namespace CodeMap.Storage.Engine.Tests;

using FluentAssertions;
using Xunit;

public sealed class AdjacencyIndexBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-adj-test-{Guid.NewGuid():N}");

    public AdjacencyIndexBuilderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string OutPath => Path.Combine(_tempDir, "adjacency-out.idx");
    private string InPath => Path.Combine(_tempDir, "adjacency-in.idx");

    [Fact]
    public void KnownGraph_OutgoingEdgesCorrect()
    {
        // Symbol 1 calls Symbol 2 (edge 1) and Symbol 3 (edge 2)
        // Symbol 2 calls Symbol 3 (edge 3)
        var edges = new EdgeRecord[]
        {
            new(1, 1, 2, 0, 1, 0, 10, 1, 0, 0, 1),
            new(2, 1, 3, 0, 1, 10, 20, 1, 0, 0, 1),
            new(3, 2, 3, 0, 1, 20, 30, 1, 0, 0, 1),
        };

        AdjacencyIndexBuilder.Build(OutPath, InPath, edges, maxSymbolId: 3);

        var outBytes = File.ReadAllBytes(OutPath);

        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 1, 3).Should().BeEquivalentTo([1, 2]);
        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 2, 3).Should().BeEquivalentTo([3]);
        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 3, 3).Should().BeEmpty();
    }

    [Fact]
    public void KnownGraph_IncomingEdgesCorrect()
    {
        var edges = new EdgeRecord[]
        {
            new(1, 1, 2, 0, 1, 0, 10, 1, 0, 0, 1),
            new(2, 1, 3, 0, 1, 10, 20, 1, 0, 0, 1),
            new(3, 2, 3, 0, 1, 20, 30, 1, 0, 0, 1),
        };

        AdjacencyIndexBuilder.Build(OutPath, InPath, edges, maxSymbolId: 3);

        var inBytes = File.ReadAllBytes(InPath);

        AdjacencyIndexBuilder.ReadEdgeIds(inBytes, 1, 3).Should().BeEmpty();
        AdjacencyIndexBuilder.ReadEdgeIds(inBytes, 2, 3).Should().BeEquivalentTo([1]);
        AdjacencyIndexBuilder.ReadEdgeIds(inBytes, 3, 3).Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void SymbolWithNoEdges_ReturnsEmpty()
    {
        var edges = new EdgeRecord[]
        {
            new(1, 1, 2, 0, 1, 0, 10, 1, 0, 0, 1),
        };

        AdjacencyIndexBuilder.Build(OutPath, InPath, edges, maxSymbolId: 5);

        var outBytes = File.ReadAllBytes(OutPath);
        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 3, 5).Should().BeEmpty();
        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 4, 5).Should().BeEmpty();
        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 5, 5).Should().BeEmpty();
    }

    [Fact]
    public void EdgeIds_SortedAscending()
    {
        // Multiple edges FROM symbol 1, added in non-sorted order by EdgeIntId
        var edges = new EdgeRecord[]
        {
            new(5, 1, 2, 0, 1, 0, 10, 1, 0, 0, 1),
            new(2, 1, 3, 0, 1, 10, 20, 1, 0, 0, 1),
            new(8, 1, 4, 0, 1, 20, 30, 1, 0, 0, 1),
        };

        AdjacencyIndexBuilder.Build(OutPath, InPath, edges, maxSymbolId: 4);

        var outBytes = File.ReadAllBytes(OutPath);
        var edgeIds = AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 1, 4);
        edgeIds.Should().BeInAscendingOrder();
        edgeIds.Should().BeEquivalentTo([2, 5, 8]);
    }

    [Fact]
    public void EmptyEdges_ProducesValidFiles()
    {
        AdjacencyIndexBuilder.Build(OutPath, InPath, Array.Empty<EdgeRecord>(), maxSymbolId: 0);

        File.Exists(OutPath).Should().BeTrue();
        File.Exists(InPath).Should().BeTrue();
    }

    [Fact]
    public void UnresolvedEdge_ToSymbolZero_NotInIncoming()
    {
        // Unresolved edge: ToSymbolIntId = 0
        var edges = new EdgeRecord[]
        {
            new(1, 1, 0, 5, 1, 0, 10, 1, 1, 0, 1), // unresolved
        };

        AdjacencyIndexBuilder.Build(OutPath, InPath, edges, maxSymbolId: 1);

        var outBytes = File.ReadAllBytes(OutPath);
        AdjacencyIndexBuilder.ReadEdgeIds(outBytes, 1, 1).Should().BeEquivalentTo([1]);

        var inBytes = File.ReadAllBytes(InPath);
        // No incoming for any symbol since ToSymbolIntId = 0
        AdjacencyIndexBuilder.ReadEdgeIds(inBytes, 1, 1).Should().BeEmpty();
    }
}
