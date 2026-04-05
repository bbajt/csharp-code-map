namespace CodeMap.Storage.Engine;

/// <summary>Sorted adjacency index for O(log N) edge lookups. Synchronous. Thread-safe.</summary>
internal interface IEngineAdjacencyIndex
{
    ReadOnlySpan<int> GetOutgoingEdgeIds(int symbolIntId);
    ReadOnlySpan<int> GetIncomingEdgeIds(int symbolIntId);
    bool HasEdge(int fromSymbolIntId, int toSymbolIntId);
}
