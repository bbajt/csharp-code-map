namespace CodeMap.Roslyn.Extraction;

using Microsoft.CodeAnalysis;

/// <summary>
/// Formats Roslyn ISymbol instances into human-readable signature strings.
/// </summary>
internal static class SignatureFormatter
{
    private static readonly SymbolDisplayFormat _format = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
                       | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                     | SymbolDisplayMemberOptions.IncludeType
                     | SymbolDisplayMemberOptions.IncludeRef
                     | SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                        | SymbolDisplayParameterOptions.IncludeName
                        | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    public static string Format(ISymbol symbol) => symbol.ToDisplayString(_format);
}
