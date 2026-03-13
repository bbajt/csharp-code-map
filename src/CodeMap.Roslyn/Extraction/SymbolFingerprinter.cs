namespace CodeMap.Roslyn.Extraction;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;

/// <summary>
/// Computes stable structural fingerprints for Roslyn ISymbol instances.
/// Fingerprints are name-independent: renames don't change them.
///
/// Format: "sym_" + 16 lowercase hex chars (8 bytes of SHA-256).
///
/// Fingerprint inputs (used):
///   SymbolKind, ContainerTypeFQN, Namespace, IsStatic,
///   ReturnTypeFQN (methods), GenericArity (methods/types),
///   ParameterTypeFQNs (methods), PropertyTypeFQN, FieldTypeFQN, EventTypeFQN
///
/// Fingerprint inputs (ignored):
///   Method name, parameter names, variable names, file path, documentation
///
/// Disambiguation: When multiple symbols in the same container share the same
/// structural fingerprint (e.g., void M(int) and void N(int) in the same class),
/// use <see cref="ComputeStableIds"/> which appends an ordinal based on source
/// declaration order. <see cref="ComputeStableId"/> returns the raw fingerprint.
/// </summary>
internal static class SymbolFingerprinter
{
    /// <summary>
    /// Computes the raw structural fingerprint for a single symbol, without
    /// ordinal disambiguation. Safe to call in isolation when the calling context
    /// guarantees no same-container collisions (e.g., single-method rename tests).
    /// </summary>
    public static StableId ComputeStableId(ISymbol symbol)
    {
        var fingerprint = BuildFingerprint(symbol, ordinal: null);
        return HashFingerprint(fingerprint);
    }

    /// <summary>
    /// Computes stable IDs for a batch of symbols, applying ordinal disambiguation
    /// for symbols that share the same structural fingerprint within the same container.
    /// Ordinals are assigned in source-declaration order (ascending source span).
    ///
    /// Use this method in the extraction pipeline to guarantee uniqueness.
    /// </summary>
    public static IReadOnlyDictionary<ISymbol, StableId> ComputeStableIds(
        IEnumerable<ISymbol> symbols,
        IEqualityComparer<ISymbol>? comparer = null)
    {
        var list = symbols.ToList();
        var result = new Dictionary<ISymbol, StableId>(
            comparer ?? SymbolEqualityComparer.Default);

        // Group by (container_fqn + base_fingerprint) to detect collisions
        var groups = list.GroupBy(s =>
        {
            var container = s.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                         ?? s.ContainingNamespace?.ToDisplayString() ?? "";
            return container + "\x00" + BuildFingerprint(s, ordinal: null);
        });

        foreach (var group in groups)
        {
            var inGroup = group
                .OrderBy(s => s.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
                .ToList();

            if (inGroup.Count == 1)
            {
                // No collision — raw fingerprint, no ordinal
                result[inGroup[0]] = HashFingerprint(BuildFingerprint(inGroup[0], ordinal: null));
            }
            else
            {
                // Collision — append ordinal by declaration order
                for (int i = 0; i < inGroup.Count; i++)
                    result[inGroup[i]] = HashFingerprint(BuildFingerprint(inGroup[i], ordinal: i));
            }
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildFingerprint(ISymbol symbol, int? ordinal)
    {
        var parts = new List<string>(10);

        parts.Add(symbol.Kind.ToString());

        var containerFqn = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "";
        parts.Add(containerFqn);

        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        parts.Add(ns);

        parts.Add(symbol.IsStatic ? "static" : "instance");

        switch (symbol)
        {
            case IMethodSymbol method:
                parts.Add(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                parts.Add(method.Arity.ToString());
                foreach (var p in method.Parameters)
                    parts.Add(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                break;

            case IPropertySymbol property:
                parts.Add(property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                foreach (var p in property.Parameters) // indexer params
                    parts.Add(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                break;

            case IFieldSymbol field:
                parts.Add(field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                break;

            case IEventSymbol evt:
                parts.Add(evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                break;

            case INamedTypeSymbol namedType:
                parts.Add(namedType.Arity.ToString());
                break;
        }

        if (ordinal.HasValue)
            parts.Add(ordinal.Value.ToString());

        return string.Join("|", parts);
    }

    private static StableId HashFingerprint(string fingerprint)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        var hex = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return new StableId("sym_" + hex);
    }
}
