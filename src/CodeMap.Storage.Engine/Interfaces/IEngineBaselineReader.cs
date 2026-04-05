namespace CodeMap.Storage.Engine;

/// <summary>Read-only view of a finalized baseline snapshot. Immutable after construction. Thread-safe. Dispose closes mmap handles.</summary>
internal interface IEngineBaselineReader : IDisposable
{
    string           CommitSha  { get; }
    BaselineManifest Manifest   { get; }
    IDictionaryReader Dictionary { get; }

    int SymbolCount  { get; }
    int FileCount    { get; }
    int ProjectCount { get; }
    int EdgeCount    { get; }
    int FactCount    { get; }

    ref readonly SymbolRecord  GetSymbolByIntId(int symbolIntId);
    SymbolRecord?  GetSymbolByStableId(string stableId);
    SymbolRecord?  GetSymbolByFqn(string fqn);
    IReadOnlyList<SymbolRecord> GetSymbolsByFile(int fileIntId);
    IEnumerable<SymbolRecord>  EnumerateSymbols();

    ref readonly FileRecord GetFileByIntId(int fileIntId);
    FileRecord?  GetFileByPath(string repoRelativePath);
    IEnumerable<FileRecord> EnumerateFiles();

    ref readonly ProjectRecord GetProjectByIntId(int projectIntId);
    IEnumerable<ProjectRecord> EnumerateProjects();

    IReadOnlyList<EdgeRecord> GetOutgoingEdges(int symbolIntId, EdgeFilter filter = default);
    IReadOnlyList<EdgeRecord> GetIncomingEdges(int symbolIntId, EdgeFilter filter = default);
    ref readonly EdgeRecord GetEdgeByIntId(int edgeIntId);
    IEnumerable<EdgeRecord> EnumerateEdges();

    IReadOnlyList<FactRecord> GetFactsBySymbol(int symbolIntId);
    IReadOnlyList<FactRecord> GetFactsByKind(int factKind);
    IEnumerable<FactRecord>   EnumerateFacts();

    IEngineSearchIndex    Search    { get; }
    IEngineAdjacencyIndex Adjacency { get; }
}
