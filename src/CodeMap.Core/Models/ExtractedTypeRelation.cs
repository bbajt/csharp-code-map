namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;

/// <summary>
/// A type-level relationship extracted during Roslyn compilation.
/// Represents a base class or interface relationship for a given type.
/// </summary>
public record ExtractedTypeRelation(
    SymbolId TypeSymbolId,
    SymbolId RelatedSymbolId,
    TypeRelationKind RelationKind,
    string DisplayName,
    Types.StableId? StableTypeId = null,
    Types.StableId? StableRelatedId = null
);
