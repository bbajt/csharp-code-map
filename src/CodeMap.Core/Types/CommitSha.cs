namespace CodeMap.Core.Types;

/// <summary>
/// Strongly-typed Git commit SHA.
/// Must be exactly 40 lowercase hex characters.
/// </summary>
public readonly record struct CommitSha
{
    public string Value { get; }

    private CommitSha(string value) => Value = value;

    /// <summary>Creates a CommitSha from a pre-validated 40-char lowercase hex string.</summary>
    /// <exception cref="ArgumentException">If value is null, empty, not 40 chars, or not lowercase hex.</exception>
    public static CommitSha From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 40)
            throw new ArgumentException($"CommitSha must be 40 characters, got {value.Length}.", nameof(value));
        if (!value.All(c => char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("CommitSha must contain only lowercase hex characters.", nameof(value));
        return new CommitSha(value);
    }

    public override string ToString() => Value;

    /// <summary>Implicit conversion to string for serialization convenience.</summary>
    public static implicit operator string(CommitSha sha) => sha.Value;
}
