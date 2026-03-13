namespace CodeMap.Roslyn.Extraction;

using Microsoft.CodeAnalysis;
using CmKind = CodeMap.Core.Enums.SymbolKind;

/// <summary>
/// Maps Roslyn ISymbol subtypes to CodeMap SymbolKind enum values.
/// Order of checks matters — IsRecord before Class/Struct, IsIndexer before Property, IsConst before Field.
/// </summary>
internal static class SymbolKindMapper
{
    public static CmKind Map(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => MapNamedType(type),
        IMethodSymbol method => MapMethod(method),
        IPropertySymbol property when property.IsIndexer => CmKind.Indexer,
        IPropertySymbol => CmKind.Property,
        IFieldSymbol field when field.IsConst => CmKind.Constant,
        IFieldSymbol => CmKind.Field,
        IEventSymbol => CmKind.Event,
        _ => CmKind.Method,
    };

    private static CmKind MapNamedType(INamedTypeSymbol type)
    {
        // Check IsRecord before TypeKind (records have TypeKind Class or Struct)
        if (type.IsRecord) return CmKind.Record;

        return type.TypeKind switch
        {
            TypeKind.Class => CmKind.Class,
            TypeKind.Struct => CmKind.Struct,
            TypeKind.Interface => CmKind.Interface,
            TypeKind.Enum => CmKind.Enum,
            TypeKind.Delegate => CmKind.Delegate,
            _ => CmKind.Class,
        };
    }

    private static CmKind MapMethod(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => CmKind.Constructor,
        MethodKind.UserDefinedOperator or MethodKind.Conversion => CmKind.Operator,
        _ => CmKind.Method,
    };
}
