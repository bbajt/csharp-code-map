namespace CodeMap.Core.Types;

/// <summary>
/// Strongly-typed symbol identifier.
/// Typically the fully-qualified symbol name (e.g., "Namespace.Class.Method").
/// </summary>
public readonly record struct SymbolId
{
    public string Value { get; }

    private SymbolId(string value) => Value = value;

    /// <summary>Creates a SymbolId from a pre-validated string.</summary>
    /// <exception cref="ArgumentException">If value is null or whitespace.</exception>
    public static SymbolId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SymbolId(value);
    }

    public override string ToString() => Value;

    /// <summary>An empty SymbolId used for unresolved syntactic references.</summary>
    public static readonly SymbolId Empty = new SymbolId("");

    /// <summary>Implicit conversion to string for serialization convenience.</summary>
    public static implicit operator string(SymbolId id) => id.Value;
}
