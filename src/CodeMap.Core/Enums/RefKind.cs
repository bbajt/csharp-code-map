namespace CodeMap.Core.Enums;

/// <summary>
/// Classification of how a symbol is referenced by another symbol.
/// </summary>
public enum RefKind
{
    /// <summary>Method invocation or delegate invocation.</summary>
    Call,

    /// <summary>Value read (identifier in read context).</summary>
    Read,

    /// <summary>Value write (assignment left-hand side).</summary>
    Write,

    /// <summary>Object creation (new T()).</summary>
    Instantiate,

    /// <summary>Method override declaration.</summary>
    Override,

    /// <summary>Interface member implementation.</summary>
    Implementation,
}
