namespace CodeMap.Query;

/// <summary>
/// Sanitizes user-supplied query strings before passing them to the search engine.
/// </summary>
internal static class FtsQuerySanitizer
{
    /// <summary>
    /// Removes or escapes patterns that cause search engine parse errors.
    /// <list type="bullet">
    ///   <item><c>^</c> prefix — treated as a special query prefix; stripped to prevent errors.</item>
    ///   <item>Unbalanced double-quotes — phrase syntax <c>"foo bar"</c> requires matched
    ///   pairs; an odd number of quotes causes a syntax error.</item>
    /// </list>
    /// Returns <see langword="null"/> if the result is empty after sanitization so the caller
    /// can return an INVALID_ARGUMENT error instead of hitting the store.
    /// </summary>
    internal static string? Sanitize(string query)
    {
        // Strip leading '^' — special query prefix that triggers parse errors.
        var sanitized = query.TrimStart('^');

        // Balance double-quote pairs. Phrase queries use "..." syntax.
        // An odd number of quotes produces a parse error; strip all quotes in that case
        // rather than silently mutating phrase intent.
        if (sanitized.Count(c => c == '"') % 2 != 0)
            sanitized = sanitized.Replace("\"", "");

        sanitized = sanitized.Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }
}
