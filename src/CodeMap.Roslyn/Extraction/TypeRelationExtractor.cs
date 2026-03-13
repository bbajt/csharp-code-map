namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;

/// <summary>
/// Walks a Roslyn Compilation and extracts type-level inheritance and interface relationships.
/// Produces <see cref="ExtractedTypeRelation"/> records for each class/struct/interface type.
/// </summary>
internal static class TypeRelationExtractor
{
    /// <summary>
    /// Extracts all type relations from the given compilation.
    /// Skips System.Object as a base type (universal, adds noise).
    /// Uses typeSymbol.Interfaces (directly declared), not AllInterfaces (includes inherited).
    /// </summary>
    public static IReadOnlyList<ExtractedTypeRelation> ExtractAll(
        Compilation compilation,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var relations = new List<ExtractedTypeRelation>();
        WalkNamespace(compilation.Assembly.GlobalNamespace, relations, stableIdMap);
        return relations;
    }

    private static void WalkNamespace(INamespaceSymbol ns, List<ExtractedTypeRelation> relations,
        IReadOnlyDictionary<string, StableId>? stableIdMap)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                WalkNamespace(childNs, relations, stableIdMap);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.IsImplicitlyDeclared) continue;

                ExtractForType(type, relations, stableIdMap);

                // Walk nested types
                foreach (var nested in type.GetTypeMembers())
                {
                    if (nested.IsImplicitlyDeclared) continue;
                    ExtractForType(nested, relations, stableIdMap);
                }
            }
        }
    }

    private static void ExtractForType(INamedTypeSymbol type, List<ExtractedTypeRelation> relations,
        IReadOnlyDictionary<string, StableId>? stableIdMap)
    {
        var typeIdStr = type.GetDocumentationCommentId();
        if (typeIdStr is null) return;

        var typeId = SymbolId.From(typeIdStr);
        var stableTypeId = stableIdMap?.TryGetValue(typeIdStr, out var stid) == true ? stid : (StableId?)null;

        // Base type (skip universal runtime base types — System.Object, ValueType, Enum — they add noise)
        if (type.BaseType is not null
            && type.BaseType.SpecialType != SpecialType.System_Object
            && type.BaseType.SpecialType != SpecialType.System_ValueType
            && type.BaseType.SpecialType != SpecialType.System_Enum)
        {
            var baseIdStr = type.BaseType.GetDocumentationCommentId();
            if (baseIdStr is not null)
            {
                var stableRelated = stableIdMap?.TryGetValue(baseIdStr, out var srid) == true ? srid : (StableId?)null;
                relations.Add(new ExtractedTypeRelation(
                    TypeSymbolId: typeId,
                    RelatedSymbolId: SymbolId.From(baseIdStr),
                    RelationKind: TypeRelationKind.BaseType,
                    DisplayName: type.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    StableTypeId: stableTypeId,
                    StableRelatedId: stableRelated));
            }
        }

        // Directly implemented interfaces (not inherited)
        foreach (var iface in type.Interfaces)
        {
            var ifaceIdStr = iface.GetDocumentationCommentId();
            if (ifaceIdStr is null) continue;

            var stableRelated = stableIdMap?.TryGetValue(ifaceIdStr, out var srid) == true ? srid : (StableId?)null;
            relations.Add(new ExtractedTypeRelation(
                TypeSymbolId: typeId,
                RelatedSymbolId: SymbolId.From(ifaceIdStr),
                RelationKind: TypeRelationKind.Interface,
                DisplayName: iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                StableTypeId: stableTypeId,
                StableRelatedId: stableRelated));
        }
    }
}
