namespace CodeMap.Query;

using CodeMap.Core.Types;

/// <summary>
/// A single node discovered during BFS traversal, with its depth and outgoing edges.
/// </summary>
public record TraversedNode(
    SymbolId SymbolId,
    int Depth,
    IReadOnlyList<SymbolId> ConnectedIds
);

/// <summary>
/// The result of a BFS graph traversal from a root symbol.
/// </summary>
public record GraphTraversalResult(
    IReadOnlyList<TraversedNode> Nodes,
    int TotalNodesFound,
    bool Truncated
);
