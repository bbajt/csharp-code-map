namespace CodeMap.Core.Models;

/// <summary>
/// Points to a specific location in source code.
/// Used to back every claim with a verifiable source reference.
/// </summary>
public record EvidencePointer
{
    public Types.RepoId RepoId { get; init; }
    public Types.FilePath FilePath { get; init; }
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public Types.SymbolId? SymbolId { get; init; }
    public string? Excerpt { get; init; }

    /// <summary>Validates that LineStart ≥ 1 and LineEnd ≥ LineStart.</summary>
    public EvidencePointer(
        Types.RepoId repoId,
        Types.FilePath filePath,
        int lineStart,
        int lineEnd,
        Types.SymbolId? symbolId = null,
        string? excerpt = null)
    {
        if (lineStart < 1)
            throw new ArgumentOutOfRangeException(nameof(lineStart), "Must be >= 1.");
        if (lineEnd < lineStart)
            throw new ArgumentOutOfRangeException(nameof(lineEnd), "Must be >= LineStart.");
        RepoId = repoId;
        FilePath = filePath;
        LineStart = lineStart;
        LineEnd = lineEnd;
        SymbolId = symbolId;
        Excerpt = excerpt;
    }
}
