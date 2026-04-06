namespace CodeMap.Core.Types;

/// <summary>
/// Strongly-typed workspace identifier.
/// Used to isolate multi-agent overlays.
/// </summary>
public readonly record struct WorkspaceId
{
    public string Value { get; }

    private WorkspaceId(string value) => Value = value;

    /// <summary>Creates a WorkspaceId from a pre-validated string.</summary>
    /// <exception cref="ArgumentException">If value is null, whitespace, or contains path traversal characters.</exception>
    public static WorkspaceId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("WorkspaceId must not contain path separators.", nameof(value));
        if (value.Contains(".."))
            throw new ArgumentException("WorkspaceId must not contain path traversal sequences.", nameof(value));
        return new WorkspaceId(value);
    }

    public override string ToString() => Value;

    /// <summary>Implicit conversion to string for serialization convenience.</summary>
    public static implicit operator string(WorkspaceId id) => id.Value;
}
