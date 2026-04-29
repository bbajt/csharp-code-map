namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction.Razor;
using Microsoft.CodeAnalysis;

/// <summary>
/// Extracts Blazor component-level facts from a Roslyn compilation:
///   <see cref="FactKind.RazorInject"/> for [Inject] properties (Value: "Name: TypeFqn"),
///   <see cref="FactKind.RazorParameter"/> for [Parameter] properties (Value: "Name: TypeFqn").
///
/// Walks the assembly's symbol table for types deriving from
/// <c>Microsoft.AspNetCore.Components.ComponentBase</c>, then inspects each property's
/// attributes. Symbols are emitted with the component class as the carrier symbol
/// so <c>surfaces.list_di_registrations</c> and <c>surfaces.list_endpoints</c>
/// can join them back to the page.
/// </summary>
internal static class RazorComponentExtractor
{
    /// <summary>
    /// Extracts <see cref="FactKind.RazorInject"/> and <see cref="FactKind.RazorParameter"/>
    /// facts for every ComponentBase derivative in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        // VB.NET Blazor is exotic and unsupported in mainstream tooling; gate on
        // C# only to keep this extractor focused.
        if (compilation.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            return [];

        var facts = new List<ExtractedFact>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var type in RazorSgHelpers.GetComponentBaseDerivatives(compilation))
        {
            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is null) continue;

            var typeId = type.GetDocumentationCommentId() ?? type.ToDisplayString();
            StableId stableId = default;
            stableIdMap?.TryGetValue(typeId, out stableId);

            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                FactKind? kind = ClassifyProperty(property);
                if (kind is null) continue;

                var typeFqn = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var propLocation = property.Locations.FirstOrDefault(l => l.IsInSource) ?? location;

                // GetMappedLineSpan respects #line directives so [Inject]/[Parameter]
                // properties declared in @code blocks resolve back to the .razor file
                // and line numbers, not the generated *_razor.g.cs offset.
                var mapped = propLocation.GetMappedLineSpan();
                var sourcePath = mapped.HasMappedPath && !string.IsNullOrEmpty(mapped.Path)
                    ? mapped.Path
                    : propLocation.SourceTree?.FilePath ?? "";
                var filePath = MakeRepoRelative(sourcePath, normalizedDir);

                facts.Add(new ExtractedFact(
                    SymbolId: SymbolId.From(typeId),
                    StableId: stableId == default ? null : stableId,
                    Kind: kind.Value,
                    Value: $"{property.Name}: {typeFqn}",
                    FilePath: filePath,
                    LineStart: mapped.StartLinePosition.Line + 1,
                    LineEnd: mapped.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }
        }

        return facts;
    }

    private static FactKind? ClassifyProperty(IPropertySymbol property)
    {
        foreach (var attr in property.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            var ns = attr.AttributeClass?.ContainingNamespace?.ToDisplayString();
            if (ns != "Microsoft.AspNetCore.Components") continue;
            if (name == "InjectAttribute") return FactKind.RazorInject;
            if (name == "ParameterAttribute") return FactKind.RazorParameter;
        }
        return null;
    }

    private static FilePath MakeRepoRelative(string filePath, string normalizedDir)
    {
        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            return FilePath.From(normalized[normalizedDir.Length..]);
        return FilePath.From(Path.GetFileName(normalized));
    }
}
