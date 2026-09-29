namespace CodeMap.Query;

/// <summary>
/// Sanitizes user-supplied <c>symbols.search</c> query strings before passing them to the search engine.
/// The engine's syntax (PHASE-21-08, ADR-055): terms (implicit AND), <c>OR</c>, optional <c>AND</c>,
/// double quotes (all quoted words), trailing <c>*</c>. It is not SQLite FTS5 (removed in v2.1.0):
/// <c>NOT</c> and <c>NEAR</c> are not supported.
/// </summary>
internal static class FtsQuerySanitizer
{
    private static readonly char[] WordSeparators = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// Removes or escapes patterns that the search can't use.
    /// <list type="bullet">
    ///   <item><c>^</c> prefix — a legacy FTS5 query prefix; stripped.</item>
    ///   <item>Unbalanced double-quotes — stripped entirely rather than guessing the intended grouping.</item>
    /// </list>
    /// Returns <see langword="null"/> if nothing searchable remains (empty, or only operators /
    /// punctuation such as <c>OR</c>, <c>AND</c>, quotes, parentheses, <c>*</c>) so the caller can
    /// return an INVALID_ARGUMENT error instead of hitting the store.
    /// </summary>
    internal static string? Sanitize(string query)
    {
        // Strip leading '^' — legacy special query prefix.
        var sanitized = query.TrimStart('^');

        // Balance double-quote pairs; an odd number is stripped rather than silently regrouped.
        if (sanitized.Count(c => c == '"') % 2 != 0)
            sanitized = sanitized.Replace("\"", "");

        sanitized = sanitized.Trim();
        return HasSearchableTerm(sanitized) ? sanitized : null;
    }

    /// <summary>
    /// The first standalone uppercase <c>NOT</c> or <c>NEAR</c> in <paramref name="query"/> (FTS5 operators
    /// the v2 search doesn't support), or <see langword="null"/>. Lowercase <c>not</c> is a search term.
    /// </summary>
    internal static string? UnsupportedOperator(string query)
    {
        foreach (var word in Words(query))
        {
            if (word is "NOT" or "NEAR")
                return word;
        }
        return null;
    }

    /// <summary>The INVALID_ARGUMENT message for an unsupported operator.</summary>
    internal static string UnsupportedOperatorMessage(string op) =>
        $"'{op}' isn't supported by symbols.search. Supported: terms (all must match), OR between " +
        "alternatives, \"quoted words\", trailing *. To narrow results use kinds / namespace / file_path / project_name.";

    /// <summary>The INVALID_ARGUMENT message for a query with nothing searchable.</summary>
    internal const string NothingSearchableMessage =
        "Query contains no searchable term (only operators or punctuation). Try a plain symbol name.";

    private static bool HasSearchableTerm(string query)
    {
        foreach (var word in Words(query))
        {
            if (word is "OR" or "AND") continue;
            if (word.Trim('*').Length > 0) return true;
        }
        return false;
    }

    private static IEnumerable<string> Words(string query) =>
        query.Replace('"', ' ').Replace('(', ' ').Replace(')', ' ')
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
}
