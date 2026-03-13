namespace SampleApp.Shared.Utilities;

/// <summary>
/// Helper utilities for CamelCase and PascalCase string operations.
/// AC-T01-04 — CamelCase compound names: searchable by "Camel", "Case", "String", "Helper".
/// </summary>
public static class CamelCaseStringHelper
{
    /// <summary>Splits a CamelCase or PascalCase string into its component words.</summary>
    public static IReadOnlyList<string> SplitWords(string input)
    {
        if (string.IsNullOrEmpty(input)) return [];

        var words = new List<string>();
        int start = 0;

        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]) && char.IsLower(input[i - 1]))
            {
                words.Add(input[start..i]);
                start = i;
            }
        }

        words.Add(input[start..]);
        return words;
    }

    /// <summary>Converts a PascalCase string to kebab-case.</summary>
    public static string ToKebabCase(string input)
        => string.Join("-", SplitWords(input)).ToLowerInvariant();

    /// <summary>Converts a PascalCase string to snake_case.</summary>
    public static string ToSnakeCase(string input)
        => string.Join("_", SplitWords(input)).ToLowerInvariant();

    /// <summary>Determines whether the input contains a given word as a CamelCase component.</summary>
    public static bool ContainsWord(string input, string word)
        => SplitWords(input).Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
}
