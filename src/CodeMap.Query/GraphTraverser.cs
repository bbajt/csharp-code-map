namespace CodeMap.Query;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;

/// <summary>
/// Pure BFS algorithm for traversing symbol reference graphs.
/// Has no storage dependencies — callers supply an <c>expandNode</c> delegate.
/// Supports cycle detection and depth + per-level limiting.
/// </summary>
public class GraphTraverser
{
    private readonly IMetadataResolver? _resolver;

    /// <summary>
    /// Maximum number of new type lazy resolutions (Level 1 or Level 2) permitted
    /// during a single BFS traversal. When the budget is exhausted, unresolved
    /// nodes are included as stubs but their outgoing edges are not expanded.
    /// Default: 20.
    /// </summary>
    public int MaxLazyResolutions { get; init; } = 20;

    /// <summary>Initializes a GraphTraverser with no lazy resolution capability.</summary>
    public GraphTraverser() { }

    /// <summary>
    /// Initializes a GraphTraverser with optional lazy DLL resolution.
    /// When <paramref name="resolver"/> is non-null and traversal repo context is supplied,
    /// BFS will attempt to decompile DLL stubs with no outgoing edges (within budget).
    /// </summary>
    public GraphTraverser(IMetadataResolver? resolver, int maxLazyResolutions = 20)
    {
        _resolver = resolver;
        MaxLazyResolutions = maxLazyResolutions;
    }

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
    /// <param name="lazyRepoId">Optional repo context for lazy DLL resolution.</param>
    /// <param name="lazyCommitSha">Optional commit context for lazy DLL resolution.</param>
    public async Task<GraphTraversalResult> TraverseAsync(
        SymbolId rootSymbolId,
        Func<SymbolId, CancellationToken, Task<IReadOnlyList<SymbolId>>> expandNode,
        int maxDepth,
        int limitPerLevel,
        CancellationToken ct = default,
        RepoId? lazyRepoId = null,
        CommitSha? lazyCommitSha = null)
    {
        // allNodes maps SymbolId → (depth, mutable edge list)
        var allNodes = new Dictionary<SymbolId, (int Depth, List<SymbolId> EdgesTo)>
        {
            [rootSymbolId] = (0, [])
        };
        var visited = new HashSet<SymbolId> { rootSymbolId };
        var currentLevel = new List<SymbolId> { rootSymbolId };
        var truncated = false;
        int lazyBudget = MaxLazyResolutions;

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

                // Budget guard: if expandNode returned nothing and we have a resolver + context,
                // attempt lazy decompilation of this DLL stub, then retry once.
                if (connectedIds.Count == 0
                    && _resolver is not null
                    && lazyRepoId.HasValue
                    && lazyCommitSha.HasValue)
                {
                    if (lazyBudget > 0)
                    {
                        lazyBudget--;
                        var path = await _resolver.TryDecompileTypeAsync(
                            parentId, lazyRepoId.Value, lazyCommitSha.Value, ct)
                            .ConfigureAwait(false);
                        if (path is not null)
                            connectedIds = await expandNode(parentId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        truncated = true;
                    }
                }

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
