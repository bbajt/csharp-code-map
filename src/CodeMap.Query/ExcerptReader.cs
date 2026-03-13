namespace CodeMap.Query;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;

/// <summary>
/// Reads one-line source excerpts from files via the symbol store.
/// Used by the query engine to populate ClassifiedReference.Excerpt.
/// </summary>
public class ExcerptReader
{
    private readonly ISymbolStore _store;

    public ExcerptReader(ISymbolStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Reads a single line from the file and returns it trimmed.
    /// Returns null if the file does not exist or the line is out of range.
    /// Lines longer than 200 characters are truncated with "...".
    /// </summary>
    public async Task<string?> ReadLineAsync(
        RepoId repoId,
        CommitSha commitSha,
        FilePath filePath,
        int lineNumber,
        CancellationToken ct = default)
    {
        if (lineNumber < 1) return null;

        var span = await _store.GetFileSpanAsync(
            repoId, commitSha, filePath, lineNumber, lineNumber, ct)
            .ConfigureAwait(false);

        if (span is null) return null;

        var line = span.Content.Trim();
        if (line.Length > 200)
            line = line[..197] + "...";

        return line;
    }
}
