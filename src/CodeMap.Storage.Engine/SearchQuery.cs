namespace CodeMap.Storage.Engine;

/// <summary>
/// A parsed <c>symbols_search</c> query: an OR of AND-groups (PHASE-21-08, ADR-055). Shared by the
/// baseline (<see cref="SearchIndexReader"/>) and the overlay (<see cref="CustomEngineOverlayStore"/>)
/// so both paths match the same symbols.
/// </summary>
/// <remarks>
/// Syntax: terms separated by whitespace are ANDed; a standalone uppercase <c>OR</c> separates
/// alternatives (lowest precedence, as in FTS5: <c>A B OR C</c> = <c>(A AND B) OR C</c>); a standalone
/// <c>AND</c> is accepted and ignored; double quotes and parentheses are ignored (a quoted phrase
/// means "all these words": the index keeps token sets, not positions); a trailing <c>*</c> on a term
/// is accepted and ignored (prefix matching is native). Lowercase <c>or</c>/<c>and</c> are terms.
/// Each term is tokenized as the v2 engine always tokenized queries (lowercased, split on separators).
/// </remarks>
internal sealed record SearchQuery(IReadOnlyList<SearchQuery.Group> Groups)
{
    private static readonly char[] WordSeparators = [' ', '\t', '\r', '\n'];
    private static readonly char[] TokenSeparators = ['.', '_', '-', '/', '\\', ' ', '\t'];

    /// <summary>
    /// One AND-group: every token must prefix-match one of a symbol's tokens.
    /// <paramref name="RawText"/> is the group's text (lowercased, quotes and trailing <c>*</c>
    /// removed), used for the exact / prefix name bonus in scoring.
    /// </summary>
    internal sealed record Group(IReadOnlyList<string> Tokens, string RawText);

    /// <summary>Parses <paramref name="query"/>; groups that end up without tokens are dropped.</summary>
    internal static SearchQuery Parse(string query)
    {
        var cleaned = query.Replace('"', ' ').Replace('(', ' ').Replace(')', ' ');
        var groups = new List<Group>();
        var words = new List<string>();

        foreach (var word in cleaned.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == "OR")
            {
                AddGroup(groups, words);
                words.Clear();
            }
            else if (word != "AND")
            {
                words.Add(word);
            }
        }
        AddGroup(groups, words);
        return new SearchQuery(groups);
    }

    private static void AddGroup(List<Group> groups, List<string> words)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var raw = new List<string>();
        foreach (var word in words)
        {
            var term = word.TrimEnd('*').ToLowerInvariant();
            if (term.Length == 0) continue;
            raw.Add(term);

            // C-017, exactly as SearchIndexReader.NormalizeQuery did: split on separators, then run
            // the index-time tokenizer on each (already lowercased) segment. Term matching stays
            // token-prefix based, so "OrderServ" still finds "orderservice".
            foreach (var seg in term.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(seg);
                foreach (var part in SearchIndexBuilder.Tokenize("", seg, null))
                {
                    if (part.Length >= 1)
                        tokens.Add(part);
                }
            }
        }

        if (tokens.Count > 0)
            groups.Add(new Group([.. tokens], string.Join(' ', raw)));
    }
}
