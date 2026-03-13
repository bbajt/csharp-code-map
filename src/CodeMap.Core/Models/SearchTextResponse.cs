namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>A single line match from a text search across source files.</summary>
public record TextMatch(
    FilePath FilePath,
    int Line,
    string Excerpt
);

/// <summary>Response payload for code.search_text.</summary>
public record SearchTextResponse(
    string Pattern,
    IReadOnlyList<TextMatch> Matches,
    int TotalFiles,
    bool Truncated
);
