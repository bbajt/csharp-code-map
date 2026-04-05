namespace CodeMap.Roslyn.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using global::FSharp.Compiler.CodeAnalysis;
using global::FSharp.Compiler.Symbols;

/// <summary>
/// Maps FSharpSymbolUse[] (from GetAllUsesOfAllSymbolsInFile) to ExtractedReference[].
/// Uses XmlDocSig for SymbolIds — same format as Roslyn doc-comment IDs.
/// </summary>
internal static class FSharpReferenceMapper
{
    public static IReadOnlyList<ExtractedReference> ExtractReferences(
        IReadOnlyList<FSharpFileAnalysis> analyses,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap,
        IReadOnlySet<string>? allSymbolIds)
    {
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';
        var refs = new List<ExtractedReference>();

        foreach (var analysis in analyses)
        {
            if (analysis.CheckResults is null) continue;

            var filePath = FSharpSymbolMapper.MakeRepoRelative(analysis.FilePath, normalizedDir);

            // Get all symbol uses in this file
            FSharpSymbolUse[] allUses;
            try { allUses = analysis.CheckResults.GetAllUsesOfAllSymbolsInFile(null).ToArray(); }
            catch { continue; /* CheckResults may be degraded */ }

            // Build definition map: sorted by start line for containing-symbol resolution
            var definitions = allUses
                .Where(u => u.IsFromDefinition)
                .OrderBy(u => u.Range.StartLine)
                .ThenBy(u => u.Range.StartColumn)
                .ToList();

            foreach (var use in allUses)
            {
                // Skip definitions — they're not references
                if (use.IsFromDefinition) continue;

                // Skip type annotations (IsFromType)
                if (use.IsFromType) continue;

                var (toSymbolId, refKind) = ClassifyUse(use);
                if (toSymbolId is null || refKind is null) continue;

                // Filter to known symbols if provided (cross-project boundary check)
                if (allSymbolIds is not null && !allSymbolIds.Contains(toSymbolId))
                    continue;

                // Find containing definition (the "from" symbol)
                var fromSymbolId = FindContainingDefinition(use, definitions);

                StableId stableFrom = default, stableTo = default;
                var hasStableFrom = stableIdMap?.TryGetValue(fromSymbolId ?? "", out stableFrom) ?? false;
                var hasStableTo = stableIdMap?.TryGetValue(toSymbolId, out stableTo) ?? false;

                refs.Add(new ExtractedReference(
                    FromSymbol: fromSymbolId is not null ? SymbolId.From(fromSymbolId) : SymbolId.Empty,
                    ToSymbol: SymbolId.From(toSymbolId),
                    Kind: refKind.Value,
                    FilePath: FilePath.From(filePath),
                    LineStart: use.Range.StartLine,
                    LineEnd: use.Range.EndLine,
                    StableFromId: hasStableFrom ? stableFrom : null,
                    StableToId: hasStableTo ? stableTo : null));
            }
        }

        return refs;
    }

    // ── Ref kind classification ─────────────────────────────────────────────

    private static (string? ToSymbolId, RefKind? Kind) ClassifyUse(FSharpSymbolUse use)
    {
        var sym = use.Symbol;

        // Function/method call
        if (sym is FSharpMemberOrFunctionOrValue mfv)
        {
            string docSig;
            try { docSig = mfv.XmlDocSig; }
            catch { return (null, null); } // XmlDocSig can throw on assembly resolution failure
            if (string.IsNullOrEmpty(docSig)) return (null, null);

            if (mfv.IsProperty)
            {
                // Property read (write detection requires parent node analysis — treat as Read)
                return (docSig, RefKind.Read);
            }

            if (mfv.IsEvent)
                return (docSig, RefKind.Read);

            // Constructor → Instantiate the containing type
            if (mfv.IsConstructor)
            {
                try
                {
                    var containingType = mfv.DeclaringEntity;
                    if (containingType is { Value: not null })
                    {
                        var typeDocSig = containingType.Value.XmlDocSig;
                        if (!string.IsNullOrEmpty(typeDocSig))
                            return (typeDocSig, RefKind.Instantiate);
                    }
                }
                catch { /* DeclaringEntity can throw */ }
                return (docSig, RefKind.Call);
            }

            // Regular function/method call
            return (docSig, RefKind.Call);
        }

        // Type reference — entity usage (e.g., in `new Foo()`, type annotations handled by IsFromType filter)
        if (sym is FSharpEntity entity)
        {
            string docSig;
            try { docSig = entity.XmlDocSig; }
            catch { return (null, null); }
            if (string.IsNullOrEmpty(docSig)) return (null, null);

            // Interface implementation is handled at type-relation level, not ref level
            return (docSig, RefKind.Instantiate);
        }

        return (null, null);
    }

    // ── Containing definition resolution ────────────────────────────────────

    /// <summary>
    /// Finds the innermost containing definition for a given use.
    /// Uses binary search on the sorted definition list to find the latest
    /// definition that starts before or at the use's line.
    /// </summary>
    private static string? FindContainingDefinition(
        FSharpSymbolUse use,
        List<FSharpSymbolUse> definitions)
    {
        var useLine = use.Range.StartLine;
        string? bestMatch = null;

        // Walk backwards from the end to find the innermost containing def
        for (int i = definitions.Count - 1; i >= 0; i--)
        {
            var def = definitions[i];
            if (def.Range.StartLine > useLine) continue;

            var sym = def.Symbol;
            string? docSig = null;

            try
            {
                if (sym is FSharpMemberOrFunctionOrValue mfv)
                    docSig = mfv.XmlDocSig;
                else if (sym is FSharpEntity entity)
                    docSig = entity.XmlDocSig;
            }
            catch { continue; }

            if (!string.IsNullOrEmpty(docSig))
            {
                bestMatch = docSig;
                break;
            }
        }

        return bestMatch;
    }
}
