namespace CodeMap.Query;

using CodeMap.Core.Types;

/// <summary>
/// Pure BFS algorithm for traversing symbol reference graphs.
/// Has no storage dependencies — callers supply an <c>expandNode</c> delegate.
/// Supports cycle detection and depth + per-level limiting.
/// </summary>
public class GraphTraverser
{
    /// <summary>
    /// Performs a breadth-first traversal starting at <paramref name="rootSymbolId"/>.
    /// </summary>
    /// <param name="rootSymbolId">Starting node.</param>
    /// <param name="expandNode">
    /// Delegate that returns the connected symbol IDs for a given node.
    /// For callers: returns <c>from_symbol_id</c> values where <c>to_symbol_id = sid</c>.
    /// For callees: returns <c>to_symbol_id</c> values where <c>from_symbol_id = sid</c>.
    /// </param>
    /// <param name="maxDepth">Maximum traversal depth (1 = only direct connections).</param>
    /// <param name="limitPerLevel">Maximum new nodes added per BFS level.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GraphTraversalResult> TraverseAsync(
        SymbolId rootSymbolId,
        Func<SymbolId, CancellationToken, Task<IReadOnlyList<SymbolId>>> expandNode,
        int maxDepth,
        int limitPerLevel,
        CancellationToken ct = default)
    {
        // allNodes maps SymbolId → (depth, mutable edge list)
        var allNodes = new Dictionary<SymbolId, (int Depth, List<SymbolId> EdgesTo)>
        {
            [rootSymbolId] = (0, [])
        };
        var visited = new HashSet<SymbolId> { rootSymbolId };
        var currentLevel = new List<SymbolId> { rootSymbolId };
        var truncated = false;

        for (var depth = 1; depth <= maxDepth; depth++)
        {
            if (currentLevel.Count == 0)
                break;

            var nextLevel = new List<SymbolId>();
            var levelNodeCount = 0;

            foreach (var parentId in currentLevel)
            {
                ct.ThrowIfCancellationRequested();

                var connectedIds = await expandNode(parentId, ct).ConfigureAwait(false);

                foreach (var connId in connectedIds)
                {
                    // Record edge from parent to connected node (always — even for cycles)
                    allNodes[parentId].EdgesTo.Add(connId);

                    if (visited.Contains(connId))
                        continue;  // cycle or cross-edge — already scheduled

                    if (levelNodeCount >= limitPerLevel)
                    {
                        truncated = true;
                        continue;  // edge recorded but node won't be expanded
                    }

                    visited.Add(connId);
                    allNodes[connId] = (depth, []);
                    nextLevel.Add(connId);
                    levelNodeCount++;
                }
            }

            currentLevel = nextLevel;
        }

        var nodes = allNodes
            .Select(kv => new TraversedNode(kv.Key, kv.Value.Depth, kv.Value.EdgesTo))
            .OrderBy(n => n.Depth)
            .ThenBy(n => n.SymbolId.Value)
            .ToList();

        // TotalNodesFound excludes the root (root is the starting point, not a "found" node)
        return new GraphTraversalResult(nodes, allNodes.Count - 1, truncated);
    }
}
