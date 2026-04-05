namespace CodeMap.Storage.Engine;

/// <summary>Mutable workspace overlay. One instance per workspace. Read methods thread-safe via ReaderWriterLockSlim.</summary>
internal interface IEngineOverlay : IDisposable
{
    string WorkspaceId        { get; }
    int    Revision           { get; }
    string BaseCommitSha      { get; }
    int    NBaselineStringIds { get; }

    string ResolveString(int stringId);
    SymbolRecord? TryGetOverlaySymbol(string stableId, out bool isTombstoned);
    IReadOnlyList<EdgeRecord>   GetOverlayOutgoingEdges(int baselineSymbolIntId);
    IReadOnlyList<EdgeRecord>   GetOverlayIncomingEdges(int baselineSymbolIntId);
    IReadOnlyList<FactRecord>   GetOverlayFacts(int symbolIntId);
    IReadOnlyList<SymbolRecord> GetOverlayNewSymbols();
    IReadOnlySet<string>        Tombstones { get; }
    IReadOnlyList<int>          GetOverlaySymbolsForTokenPrefix(string tokenPrefix);
    FileRecord?                 TryGetOverlayFile(string repoRelativePath);

    IOverlayWriteBatch BeginBatch();
    Task CheckpointIfNeededAsync(CancellationToken ct = default);
}

/// <summary>Atomic write batch for overlay mutations. All mutations held in memory until CommitAsync(). Not thread-safe.</summary>
internal interface IOverlayWriteBatch : IDisposable
{
    int  InternString(string value);
    void UpsertSymbol(SymbolRecord record, string[] tokens);
    void AddEdge(EdgeRecord record);
    void AddFact(FactRecord record);
    void UpsertFile(FileRecord record);
    void Tombstone(int entityKind, int entityIntId, string? stableId = null);
    void ResolveEdge(int fromSymbolIntId, int fileIntId, int spanStart, int resolvedToSymbolIntId);
    Task CommitAsync(CancellationToken ct = default);
}
