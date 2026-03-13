namespace CodeMap.Core.Enums;

/// <summary>
/// Describes the kind of type-level relationship between two symbols.
/// </summary>
public enum TypeRelationKind
{
    /// <summary>The type directly extends a base class.</summary>
    BaseType,

    /// <summary>The type directly implements an interface.</summary>
    Interface,
}
