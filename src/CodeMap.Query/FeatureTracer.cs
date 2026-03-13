namespace CodeMap.Query;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Composes BFS call graph traversal with architectural fact annotation
/// to produce a hierarchical feature trace tree.
/// </summary>
public sealed class FeatureTracer
{
    private readonly ISymbolStore _store;
    private readonly GraphTraverser _traverser;

    public FeatureTracer(ISymbolStore store, GraphTraverser traverser)
    {
        _store = store;
        _traverser = traverser;
    }

    /// <summary>
    /// Traces a feature starting from the given entry point, returning a tree of
    /// callee nodes annotated with architectural facts.
    /// </summary>
    /// <param name="factFetcher">
    /// Optional override for fact fetching — used by workspace mode to inject overlay-aware
    /// fact retrieval. Defaults to <see cref="ISymbolStore.GetFactsForSymbolAsync"/>.
    /// </param>
    public async Task<Result<FeatureTraceResponse, CodeMapError>> TraceAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId entryPoint,
        int depth,
        int limit,
        Func<SymbolId, CancellationToken, Task<IReadOnlyList<StoredFact>>>? factFetcher = null,
        CancellationToken ct = default)
    {
        // 1. Validate entry point
        var card = await _store.GetSymbolAsync(repoId, commitSha, entryPoint, ct).ConfigureAwait(false);
        if (card is null)
            return Result<FeatureTraceResponse, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", entryPoint.Value));

        // 2. BFS callees traversal
        var bfsResult = await _traverser.TraverseAsync(
            entryPoint,
            async (sid, token) =>
            {
                var refs = await _store.GetOutgoingReferencesAsync(
                    repoId, commitSha, sid, null, limit * 2, token).ConfigureAwait(false);
                return refs
                    .Where(r => r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate)
                    .Select(r => r.ToSymbol)
                    .Distinct()
                    .Take(limit)
                    .ToList();
            },
            maxDepth: depth,
            limitPerLevel: limit,
            ct).ConfigureAwait(false);

        // 3. Collect all symbol IDs from BFS (including root)
        var allSymbolIds = bfsResult.Nodes.Select(n => n.SymbolId).ToHashSet();
        allSymbolIds.Add(entryPoint);

        // 4. Batch-fetch facts for all symbols
        var actualFetcher = factFetcher
            ?? ((sid, token) => _store.GetFactsForSymbolAsync(repoId, commitSha, sid, token));

        var factsBySymbol = new Dictionary<SymbolId, IReadOnlyList<StoredFact>>();
        foreach (var sid in allSymbolIds)
        {
            var facts = await actualFetcher(sid, ct).ConfigureAwait(false);
            if (facts.Count > 0)
                factsBySymbol[sid] = facts;
        }

        // 5. Batch-fetch display info (cards) for all symbols
        var cardsBySymbol = new Dictionary<SymbolId, SymbolCard>();
        cardsBySymbol[entryPoint] = card;
        foreach (var sid in allSymbolIds)
        {
            if (cardsBySymbol.ContainsKey(sid)) continue;
            var c = await _store.GetSymbolAsync(repoId, commitSha, sid, ct).ConfigureAwait(false);
            if (c is not null)
                cardsBySymbol[sid] = c;
        }

        // 6. Extract entry point route annotation (if endpoint)
        var entryRoute = factsBySymbol.GetValueOrDefault(entryPoint)?
            .FirstOrDefault(f => f.Kind == FactKind.Route)?.Value;

        // 7. Build tree from BFS flat results
        // nodeBySymbolId gives us each node's ConnectedIds (children) and depth
        var nodeBySymbolId = bfsResult.Nodes.ToDictionary(n => n.SymbolId);
        var rootTraceNode = BuildTree(entryPoint, 0, nodeBySymbolId, factsBySymbol, cardsBySymbol, depth);

        return Result<FeatureTraceResponse, CodeMapError>.Success(
            new FeatureTraceResponse(
                EntryPoint: entryPoint,
                EntryPointName: card.FullyQualifiedName,
                EntryPointRoute: ParseDisplayValue(entryRoute),
                Nodes: [rootTraceNode],
                TotalNodesTraversed: allSymbolIds.Count,
                Depth: depth,
                Truncated: bfsResult.Truncated));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static TraceNode BuildTree(
        SymbolId symbolId,
        int depth,
        Dictionary<SymbolId, TraversedNode> nodeBySymbolId,
        Dictionary<SymbolId, IReadOnlyList<StoredFact>> factsBySymbol,
        Dictionary<SymbolId, SymbolCard> cardsBySymbol,
        int maxDepth)
    {
        var card = cardsBySymbol.GetValueOrDefault(symbolId);
        var displayName = card?.Signature ?? symbolId.Value;
        var stableId = card?.StableId;

        var annotations = factsBySymbol.GetValueOrDefault(symbolId)?
            .Select(f => new TraceAnnotation(
                Kind: f.Kind.ToString(),
                Value: ParseDisplayValue(f.Value) ?? f.Value,
                Confidence: f.Confidence))
            .ToList<TraceAnnotation>() ?? [];

        // Recurse into children (ConnectedIds from BFS)
        var children = new List<TraceNode>();
        if (nodeBySymbolId.TryGetValue(symbolId, out var traversedNode) && depth < maxDepth)
        {
            foreach (var childId in traversedNode.ConnectedIds)
            {
                children.Add(BuildTree(childId, depth + 1, nodeBySymbolId, factsBySymbol, cardsBySymbol, maxDepth));
            }
        }

        return new TraceNode(symbolId, stableId, displayName, depth, annotations, children);
    }

    /// <summary>
    /// Strips pipe-separated metadata from fact values, returning the human-readable portion.
    /// E.g., "App:MaxRetries|GetValue" → "App:MaxRetries", "GET /api/orders" → "GET /api/orders".
    /// </summary>
    internal static string? ParseDisplayValue(string? value)
    {
        if (value is null) return null;
        var pipe = value.IndexOf('|');
        return pipe >= 0 ? value[..pipe].Trim() : value;
    }
}
