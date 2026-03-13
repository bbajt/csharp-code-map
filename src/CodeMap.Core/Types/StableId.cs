namespace CodeMap.Core.Types;

/// <summary>
/// Structural fingerprint of a symbol — stable across renames, file moves,
/// and other name-only changes. Format: "sym_" + 16 lowercase hex chars.
/// </summary>
public readonly record struct StableId(string Value)
{
    public static StableId Empty => new("");

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}
