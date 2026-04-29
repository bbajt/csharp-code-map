namespace CodeMap.Roslyn.Extraction.Razor;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

/// <summary>
/// Shared helpers for Razor / Blazor extraction: assembly-wide type enumeration
/// and ComponentBase inheritance detection. Used by <c>SymbolExtractor</c> (to
/// filter generated boilerplate), <c>EndpointExtractor</c> (Blazor @page pass),
/// and <c>RazorComponentExtractor</c> (Inject/Parameter facts).
/// </summary>
internal static class RazorSgHelpers
{
    private const string ComponentBaseFqn = "Microsoft.AspNetCore.Components.ComponentBase";

    private static readonly ConditionalWeakTable<Compilation, IReadOnlyList<INamedTypeSymbol>> _componentCache = new();

    // Razor SG emits exactly one #pragma checksum directive at (or near) the top of
    // each generated file: `#pragma checksum "<path>" "<guid>" "<sha-hex>"`. Allow
    // leading whitespace (defensive) and validate the trailing two quoted tokens
    // so noise like `// #pragma checksum "fake.razor"` in user comments doesn't
    // accidentally match.
    private static readonly Regex _checksumRegex = new(
        """^\s*#pragma\s+checksum\s+"([^"]+)"\s+"[^"]+"\s+"[^"]+"\s*$""",
        RegexOptions.Compiled);

    private const int ChecksumScanLines = 10;

    /// <summary>
    /// Parses the leading <c>#pragma checksum "&lt;path&gt;" "&lt;guid&gt;" "&lt;hash&gt;"</c>
    /// directive emitted by the Razor source generator and returns the original
    /// <c>.razor</c> path. Scans the first <see cref="ChecksumScanLines"/> lines so
    /// preludes like <c>// &lt;auto-generated&gt;</c> or BOMs don't defeat detection.
    /// Returns <c>null</c> when the directive is absent or malformed.
    /// </summary>
    public static string? ParseChecksumPath(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;

        int lineStart = 0;
        for (int i = 0; i < ChecksumScanLines && lineStart < content.Length; i++)
        {
            var newline = content.IndexOf('\n', lineStart);
            var line = newline >= 0
                ? content[lineStart..newline].TrimEnd('\r')
                : content[lineStart..];

            var match = _checksumRegex.Match(line);
            if (match.Success) return match.Groups[1].Value;

            if (newline < 0) break;
            lineStart = newline + 1;
        }
        return null;
    }

    /// <summary>
    /// Returns every ComponentBase-derived type in the compilation's assembly,
    /// computed once and cached for the lifetime of the <see cref="Compilation"/>.
    /// Both <c>EndpointExtractor</c> (Blazor @page pass) and
    /// <c>RazorComponentExtractor</c> ([Inject]/[Parameter] pass) share this list,
    /// so the assembly is walked at most once per project.
    /// </summary>
    public static IReadOnlyList<INamedTypeSymbol> GetComponentBaseDerivatives(Compilation compilation)
    {
        return _componentCache.GetValue(compilation, static c =>
        {
            var result = new List<INamedTypeSymbol>();
            foreach (var type in EnumerateAllTypes(c.Assembly.GlobalNamespace))
            {
                if (InheritsComponentBase(type))
                    result.Add(type);
            }
            return result;
        });
    }

    /// <summary>
    /// Recursively enumerates every named type in a namespace tree, including
    /// nested types. Yields one <see cref="INamedTypeSymbol"/> per declaration.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var t in EnumerateAllTypes(child)) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in type.GetTypeMembers()) yield return nested;
            }
        }
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> derives, directly or transitively,
    /// from <c>Microsoft.AspNetCore.Components.ComponentBase</c>. Used to recognise
    /// Blazor backing classes regardless of the immediate base.
    /// </summary>
    public static bool InheritsComponentBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == ComponentBaseFqn)
                return true;
        }
        return false;
    }
}
