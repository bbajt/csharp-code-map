namespace CodeMap.Core.Types;

/// <summary>
/// Strongly-typed repo-relative file path.
/// Uses forward slashes only, no leading slash.
/// </summary>
public readonly record struct FilePath
{
    public string Value { get; }

    private FilePath(string value) => Value = value;

    /// <summary>Creates a FilePath from a pre-validated string.</summary>
    /// <exception cref="ArgumentException">If value is null, whitespace, contains backslashes, or has a leading slash.</exception>
    public static FilePath From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\\'))
            throw new ArgumentException("FilePath must use forward slashes only.", nameof(value));
        if (value.StartsWith('/'))
            throw new ArgumentException("FilePath must be repo-relative (no leading slash).", nameof(value));
        return new FilePath(value);
    }

    public override string ToString() => Value;

    /// <summary>Implicit conversion to string for serialization convenience.</summary>
    public static implicit operator string(FilePath path) => path.Value;
}
