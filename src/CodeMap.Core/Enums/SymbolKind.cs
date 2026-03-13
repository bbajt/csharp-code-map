namespace CodeMap.Core.Enums;

/// <summary>
/// The kind of C# symbol.
/// </summary>
public enum SymbolKind
{
    /// <summary>A class declaration.</summary>
    Class,

    /// <summary>A struct declaration.</summary>
    Struct,

    /// <summary>An interface declaration.</summary>
    Interface,

    /// <summary>An enum declaration.</summary>
    Enum,

    /// <summary>A delegate declaration.</summary>
    Delegate,

    /// <summary>A record or record struct declaration.</summary>
    Record,

    /// <summary>A method declaration.</summary>
    Method,

    /// <summary>A property declaration.</summary>
    Property,

    /// <summary>A field declaration.</summary>
    Field,

    /// <summary>An event declaration.</summary>
    Event,

    /// <summary>A constant field or enum member.</summary>
    Constant,

    /// <summary>A constructor declaration.</summary>
    Constructor,

    /// <summary>An indexer declaration.</summary>
    Indexer,

    /// <summary>An operator overload declaration.</summary>
    Operator,
}
