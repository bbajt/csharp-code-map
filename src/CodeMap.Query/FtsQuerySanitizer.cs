namespace CodeMap.Query;

/// <summary>
/// Sanitizes user-supplied query strings before passing them to SQLite FTS5 MATCH clauses.
/// </summary>
internal static class FtsQuerySanitizer
{
    /// <summary>
    /// Removes or escapes patterns that cause SQLite FTS5 parse errors.
    /// <list type="bullet">
    ///   <item><c>^</c> prefix — FTS5 treats it as a special query (e.g. trigram) and throws
    ///   <c>unknown special query</c> for unrecognised types.</item>
    ///   <item>Unbalanced double-quotes — FTS5 phrase syntax <c>"foo bar"</c> requires matched
    ///   pairs; an odd number of quotes causes a syntax error.</item>
    /// </list>
    /// Returns <see langword="null"/> if the result is empty after sanitization so the caller
    /// can return an INVALID_ARGUMENT error instead of hitting the store.
    /// </summary>
    internal static string? Sanitize(string query)
    {
        // Strip leading '^' — FTS5 special query prefix that triggers 'unknown special query'
        // for any type CodeMap does not register (i.e. all of them).
        var sanitized = query.TrimStart('^');

        // Balance double-quote pairs. FTS5 phrase queries use "..." syntax.
        // An odd number of quotes produces a parse error; strip all quotes in that case
        // rather than silently mutating phrase intent.
        if (sanitized.Count(c => c == '"') % 2 != 0)
            sanitized = sanitized.Replace("\"", "");

        sanitized = sanitized.Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }
}
