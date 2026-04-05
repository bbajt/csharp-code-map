namespace CodeMap.Storage.Engine;

/// <summary>Search index over symbols and file content. Synchronous. Thread-safe (immutable baseline data).</summary>
internal interface IEngineSearchIndex
{
    SymbolSearchResult[] SearchSymbols(string query, SymbolSearchFilter filter);
    (TextMatch[] Matches, bool IsTruncated) SearchText(string pattern, TextSearchFilter filter);
}
