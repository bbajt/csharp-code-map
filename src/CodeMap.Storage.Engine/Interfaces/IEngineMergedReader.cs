namespace CodeMap.Storage.Engine;

/// <summary>Merged query view: overlay tombstones + replacements applied over baseline. Thread-safe. All methods synchronous.</summary>
internal interface IEngineMergedReader
{
    IEngineBaselineReader Baseline { get; }
    IEngineOverlay?       Overlay  { get; }

    SymbolRecord? GetSymbolByStableId(string stableId);
    SymbolRecord? GetSymbolByFqn(string fqn);
    SymbolRecord? GetSymbolByIntId(int symbolIntId);

    IEnumerable<SymbolRecord>    EnumerateSymbols(short? kindFilter = null, bool excludeDecompiled = false, bool excludeTestSymbols = false);
    IReadOnlyList<SymbolRecord>  GetSymbolsByFile(string repoRelativePath);

    FileRecord?              GetFileByPath(string repoRelativePath);
    IEnumerable<FileRecord>  EnumerateFiles();
    IEnumerable<string>      EnumerateFilePaths();

    IReadOnlyList<EdgeRecord> GetOutgoingEdges(int symbolIntId, EdgeFilter filter = default);
    IReadOnlyList<EdgeRecord> GetIncomingEdges(int symbolIntId, EdgeFilter filter = default);

    IReadOnlyList<FactRecord> GetFactsBySymbol(int symbolIntId);
    IReadOnlyList<FactRecord> GetFactsByKind(int factKind);

    SymbolSearchResult[]                    SearchSymbols(string query, SymbolSearchFilter filter);
    (TextMatch[] Matches, bool IsTruncated) SearchText(string pattern, TextSearchFilter filter);

    string ResolveString(int stringId);
}
