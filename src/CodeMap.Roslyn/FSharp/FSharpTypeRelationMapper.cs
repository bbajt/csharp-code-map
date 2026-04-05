namespace CodeMap.Roslyn.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using global::FSharp.Compiler.Symbols;
using global::Microsoft.FSharp.Core;

/// <summary>
/// Extracts type hierarchy (base types, interfaces) from FCS entities.
/// Maps to ExtractedTypeRelation using XmlDocSig as SymbolId.
/// </summary>
internal static class FSharpTypeRelationMapper
{
    public static IReadOnlyList<ExtractedTypeRelation> ExtractTypeRelations(
        IReadOnlyList<FSharpFileAnalysis> analyses,
        IReadOnlyDictionary<string, StableId> stableIdMap)
    {
        var relations = new List<ExtractedTypeRelation>();

        foreach (var analysis in analyses)
        {
            if (analysis.CheckResults is null) continue;

            try
            {
                foreach (var entity in analysis.CheckResults.PartialAssemblySignature.Entities)
                {
                    ExtractForEntity(entity, stableIdMap, relations);
                }
            }
            catch { /* PartialAssemblySignature can throw "not available" */ }
        }

        return relations;
    }

    private static void ExtractForEntity(
        FSharpEntity entity,
        IReadOnlyDictionary<string, StableId> stableIdMap,
        List<ExtractedTypeRelation> relations)
    {
        if (entity.IsCompilerGenerated()) return;

        var typeDocSig = entity.XmlDocSig;
        if (string.IsNullOrEmpty(typeDocSig)) return;

        var typeSymbolId = SymbolId.From(typeDocSig);
        stableIdMap.TryGetValue(typeDocSig, out var stableTypeId);

        // Base type (skip System.Object — every class inherits it)
        try
        {
            var baseTypeOpt = entity.BaseType;
#pragma warning disable CS8602 // FSharpOption.Value is safe after IsSome check
            var baseTypeValue = FSharpOption<FSharpType>.get_IsSome(baseTypeOpt) ? baseTypeOpt.Value : null;
#pragma warning restore CS8602
            if (baseTypeValue is not null && !IsSystemObject(baseTypeValue))
            {
                var baseType = baseTypeValue;
                var baseDocSig = TryGetXmlDocSig(baseType);
                if (baseDocSig != null)
                {
                    stableIdMap.TryGetValue(baseDocSig, out var stableRelatedId);
                    relations.Add(new ExtractedTypeRelation(
                        TypeSymbolId: typeSymbolId,
                        RelatedSymbolId: SymbolId.From(baseDocSig),
                        RelationKind: TypeRelationKind.BaseType,
                        DisplayName: baseType.TypeDefinition.DisplayName,
                        StableTypeId: stableTypeId,
                        StableRelatedId: stableRelatedId));
                }
            }
        }
        catch { /* BaseType can throw for some F# entities (e.g., modules) */ }

        // Implemented interfaces
        try
        {
            foreach (var iface in entity.DeclaredInterfaces)
            {
                var ifaceDocSig = TryGetXmlDocSig(iface);
                if (ifaceDocSig == null) continue;

                stableIdMap.TryGetValue(ifaceDocSig, out var stableRelatedId);
                relations.Add(new ExtractedTypeRelation(
                    TypeSymbolId: typeSymbolId,
                    RelatedSymbolId: SymbolId.From(ifaceDocSig),
                    RelationKind: TypeRelationKind.Interface,
                    DisplayName: iface.TypeDefinition.DisplayName,
                    StableTypeId: stableTypeId,
                    StableRelatedId: stableRelatedId));
            }
        }
        catch { /* DeclaredInterfaces can throw */ }

        // Recurse into nested entities
        foreach (var nested in entity.NestedEntities)
        {
            ExtractForEntity(nested, stableIdMap, relations);
        }
    }

    private static bool IsSystemObject(FSharpType type)
    {
        try
        {
            var def = type.TypeDefinition;
            return def.FullName == "System.Object" || def.FullName == "obj";
        }
        catch { return false; }
    }

    private static string? TryGetXmlDocSig(FSharpType type)
    {
        try
        {
            var sig = type.TypeDefinition.XmlDocSig;
            return string.IsNullOrEmpty(sig) ? null : sig;
        }
        catch { return null; }
    }
}
