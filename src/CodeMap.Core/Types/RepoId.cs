namespace CodeMap.Core.Types;

/// <summary>
/// Strongly-typed repository identifier.
/// Derived from remote URL hash or absolute path hash.
/// </summary>
public readonly record struct RepoId
{
    public string Value { get; }

    private RepoId(string value) => Value = value;

    /// <summary>Creates a RepoId from a pre-validated string.</summary>
    /// <exception cref="ArgumentException">If value is null or whitespace.</exception>
    public static RepoId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new RepoId(value);
    }

    public override string ToString() => Value;

    /// <summary>Implicit conversion to string for serialization convenience.</summary>
    public static implicit operator string(RepoId id) => id.Value;
}
