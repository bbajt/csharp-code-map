namespace SampleApp.Extensions;

/// <summary>Extension methods for string manipulation.</summary>
public static class StringExtensions
{
    /// <summary>Truncates a string to the specified maximum length.</summary>
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public static string ToSlug(this string value)
    {
        return value.ToLowerInvariant().Replace(' ', '-');
    }
}
