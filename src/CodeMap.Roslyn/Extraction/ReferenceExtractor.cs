namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeMapRefKind = CodeMap.Core.Enums.RefKind;

/// <summary>
/// Walks a Roslyn Compilation and extracts all inter-symbol references.
/// </summary>
internal static class ReferenceExtractor
{
    public static IReadOnlyList<ExtractedReference> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null,
        IReadOnlySet<string>? crossProjectSymbolIds = null)
    {
        var references = new List<ExtractedReference>();

        // Walk all syntax trees for expression-level references
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                var classified = RefKindClassifier.TryClassify(node, model);
                if (classified is null) continue;

                var (targetSymbol, refKind) = classified.Value;

                // Only track references to source-defined symbols
                if (!IsSourceSymbol(targetSymbol, compilation, crossProjectSymbolIds)) continue;

                var fromSymbol = FindContainingSymbol(node, model);
                if (fromSymbol is null) continue;

                var fromId = GetSymbolId(fromSymbol);
                var toId = GetSymbolId(targetSymbol);
                if (fromId is null || toId is null) continue;

                var location = node.GetLocation();
                var filePathN = GetFilePath(location, solutionDir);
                if (filePathN is null) continue;

                var lineSpan = location.GetLineSpan();
                int lineStart = lineSpan.StartLinePosition.Line + 1;
                int lineEnd = lineSpan.EndLinePosition.Line + 1;

                StableId? stableFrom = stableIdMap?.TryGetValue(fromId, out var sfid) == true ? sfid : null;
                StableId? stableTo = stableIdMap?.TryGetValue(toId, out var stid) == true ? stid : null;

                references.Add(new ExtractedReference(
                    FromSymbol: SymbolId.From(fromId),
                    ToSymbol: SymbolId.From(toId),
                    Kind: refKind,
                    FilePath: filePathN.Value,
                    LineStart: lineStart,
                    LineEnd: lineEnd,
                    StableFromId: stableFrom,
                    StableToId: stableTo
                ));
            }
        }

        // Walk declarations for Override and Implementation relationships
        ExtractDeclarationRelationships(compilation, references, solutionDir, stableIdMap, crossProjectSymbolIds);

        return references;
    }

    private static void ExtractDeclarationRelationships(Compilation compilation,
        List<ExtractedReference> references,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap,
        IReadOnlySet<string>? crossProjectSymbolIds = null)
    {
        foreach (var type in GetAllSourceTypes(compilation))
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol method)
                {
                    // Override relationship
                    if (method.IsOverride && method.OverriddenMethod is { } overridden
                        && IsSourceSymbol(overridden, compilation, crossProjectSymbolIds))
                    {
                        AddDeclRef(method, overridden, CodeMapRefKind.Override, references, solutionDir, stableIdMap);
                    }

                    // Interface implementation relationships
                    foreach (var iface in type.AllInterfaces)
                    {
                        foreach (var ifaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                        {
                            var impl = type.FindImplementationForInterfaceMember(ifaceMember);
                            if (impl is IMethodSymbol implMethod &&
                                SymbolEqualityComparer.Default.Equals(implMethod, method))
                            {
                                AddDeclRef(method, ifaceMember, CodeMapRefKind.Implementation, references, solutionDir, stableIdMap);
                            }
                        }
                    }
                }
            }
        }
    }

    private static void AddDeclRef(IMethodSymbol from, IMethodSymbol to,
        CodeMapRefKind kind, List<ExtractedReference> references,
        string solutionDir, IReadOnlyDictionary<string, StableId>? stableIdMap)
    {
        var fromId = GetSymbolId(from);
        var toId = GetSymbolId(to);
        if (fromId is null || toId is null) return;

        var loc = from.Locations.FirstOrDefault(l => l.IsInSource);
        var fpN = loc is not null ? GetFilePath(loc, solutionDir) : null;
        if (fpN is null) return;

        StableId? stableFrom = stableIdMap?.TryGetValue(fromId, out var sfid) == true ? sfid : null;
        StableId? stableTo = stableIdMap?.TryGetValue(toId, out var stid) == true ? stid : null;

        var ls = loc!.GetLineSpan();
        references.Add(new ExtractedReference(
            SymbolId.From(fromId), SymbolId.From(toId),
            kind, fpN.Value,
            ls.StartLinePosition.Line + 1,
            ls.EndLinePosition.Line + 1,
            StableFromId: stableFrom,
            StableToId: stableTo));
    }

    private static ISymbol? FindContainingSymbol(SyntaxNode node, SemanticModel model)
    {
        // Use language-agnostic GetDeclaredSymbol — works for both C# and VB.NET.
        var current = node.Parent;
        while (current is not null)
        {
            var declared = model.GetDeclaredSymbol(current);
            if (declared is IMethodSymbol or IPropertySymbol)
                return declared;
            current = current.Parent;
        }

        // Fallback: find containing type
        current = node.Parent;
        while (current is not null)
        {
            var declared = model.GetDeclaredSymbol(current);
            if (declared is INamedTypeSymbol)
                return declared;
            current = current.Parent;
        }

        return null;
    }

    private static bool IsSourceSymbol(ISymbol symbol, Compilation compilation,
        IReadOnlySet<string>? crossProjectSymbolIds = null)
    {
        // Accept symbols from the same assembly (intra-project refs)
        if (SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
            return true;
        // Accept symbols from same-language referenced projects (CompilationReference — source locations preserved).
        // Framework symbols (System.*, Microsoft.*) have no source locations so this excludes them.
        if (symbol.Locations.Any(l => l.IsInSource))
            return true;
        // Accept symbols from cross-language referenced projects (MetadataReference — no source locations).
        // Roslyn compiles C# projects to DLL before referencing from VB (and vice-versa), so source
        // locations are absent. We fall back to doc-comment ID lookup against the known symbol set
        // built from all projects in the solution that were processed before this one.
        if (crossProjectSymbolIds is not null)
        {
            var docId = symbol.GetDocumentationCommentId();
            if (docId is not null && crossProjectSymbolIds.Contains(docId))
                return true;
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllSourceTypes(Compilation compilation) =>
        GetTypesFromNamespace(compilation.Assembly.GlobalNamespace);

    private static IEnumerable<INamedTypeSymbol> GetTypesFromNamespace(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
                foreach (var t in GetTypesFromNamespace(childNs))
                    yield return t;
            else if (member is INamedTypeSymbol type)
                yield return type;
        }
    }

    private static string? GetSymbolId(ISymbol symbol) =>
        symbol.GetDocumentationCommentId()
        ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static FilePath? GetFilePath(Location location, string solutionDir)
    {
        if (!location.IsInSource || location.SourceTree is null) return null;
        string path = location.SourceTree.FilePath.Replace('\\', '/');
        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        if (Path.IsPathRooted(location.SourceTree.FilePath))
        {
            string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';
            if (path.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                return FilePath.From(path[normalizedDir.Length..]);
            return FilePath.From(fileName);
        }

        return FilePath.From(path.TrimStart('/'));
    }
}
