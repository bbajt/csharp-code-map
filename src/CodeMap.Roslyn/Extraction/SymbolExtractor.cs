namespace CodeMap.Roslyn.Extraction;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction.Razor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks a Roslyn Compilation and extracts all user-defined symbols as SymbolCard records.
/// </summary>
internal static class SymbolExtractor
{
    public static IReadOnlyList<SymbolCard> ExtractAll(Compilation compilation, string projectName, string solutionDir = "")
    {
        var (cards, _) = ExtractAllWithStableIds(compilation, projectName, solutionDir);
        return cards;
    }

    /// <summary>
    /// Extracts all symbols and computes stable structural fingerprints (SSID) for each.
    /// Returns both the patched cards and a SymbolId.Value → StableId map for use by
    /// ReferenceExtractor and TypeRelationExtractor.
    /// </summary>
    internal static (IReadOnlyList<SymbolCard> Cards, IReadOnlyDictionary<string, StableId> StableIdMap)
        ExtractAllWithStableIds(Compilation compilation, string projectName, string solutionDir = "")
    {
        var pairs = new List<(ISymbol Symbol, SymbolCard Card)>();
        WalkNamespace(compilation.Assembly.GlobalNamespace, compilation, pairs, projectName, solutionDir);

        // Compute stable fingerprints in batch (handles same-container ordinal disambiguation)
        var stableIds = SymbolFingerprinter.ComputeStableIds(pairs.Select(p => p.Symbol));

        var stableIdMap = new Dictionary<string, StableId>(StringComparer.Ordinal);
        var patchedCards = new List<SymbolCard>(pairs.Count);

        foreach (var (symbol, card) in pairs)
        {
            if (stableIds.TryGetValue(symbol, out var sid))
            {
                stableIdMap[card.SymbolId.Value] = sid;
                patchedCards.Add(card with { StableId = sid });
            }
            else
            {
                patchedCards.Add(card);
            }
        }

        return (patchedCards, stableIdMap);
    }

    private static void WalkNamespace(INamespaceSymbol ns, Compilation compilation,
        List<(ISymbol Symbol, SymbolCard Card)> pairs, string projectName, string solutionDir)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                WalkNamespace(childNs, compilation, pairs, projectName, solutionDir);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (ShouldSkip(type)) continue;

                var card = BuildCard(type, compilation, projectName, containingType: null, solutionDir);
                if (card is not null) pairs.Add((type, card));

                foreach (var typeMember in type.GetMembers())
                {
                    if (ShouldSkip(typeMember)) continue;
                    var memberCard = BuildCard(typeMember, compilation, projectName, containingType: type, solutionDir);
                    if (memberCard is not null) pairs.Add((typeMember, memberCard));
                }
            }
        }
    }

    private static bool ShouldSkip(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared) return true;
        if (symbol.DeclaredAccessibility == Accessibility.NotApplicable) return true;

        // Skip property/event accessors
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind is MethodKind.PropertyGet
                or MethodKind.PropertySet
                or MethodKind.EventAdd
                or MethodKind.EventRemove
                or MethodKind.EventRaise)
                return true;
        }

        if (IsRazorGeneratedBoilerplate(symbol)) return true;

        return false;
    }

    /// <summary>
    /// Suppresses symbols emitted by the Razor source generator that have no
    /// agent-relevant content: synthetic _Imports classes, BuildRenderTree methods
    /// on ComponentBase derivatives, and double-underscore-prefixed nested attribute
    /// types (e.g. __PrivateComponentRenderModeAttribute). Keeps user-written @code
    /// methods intact.
    /// </summary>
    private static bool IsRazorGeneratedBoilerplate(ISymbol symbol)
    {
        // Synthetic _Imports class generated per namespace from _Imports.razor.
        // The class itself plus its Execute method carry no user code.
        if (symbol is INamedTypeSymbol importsType
            && importsType.Name == "_Imports"
            && IsRazorGeneratedSource(importsType))
            return true;

        if (symbol is IMethodSymbol importsExec
            && importsExec.Name == "Execute"
            && importsExec.ContainingType is { Name: "_Imports" } importsContainer
            && IsRazorGeneratedSource(importsContainer))
            return true;

        // Double-underscore-prefixed nested types on ComponentBase derivatives
        // (e.g. __PrivateComponentRenderModeAttribute). Always SDK plumbing.
        if (symbol is INamedTypeSymbol nestedType
            && nestedType.Name.StartsWith("__", StringComparison.Ordinal)
            && nestedType.ContainingType is { } container
            && RazorSgHelpers.InheritsComponentBase(container))
            return true;

        // BuildRenderTree on any ComponentBase derivative — compiler-generated rendering
        // logic. Match by inheritance to avoid hiding identically-named user methods.
        if (symbol is IMethodSymbol m
            && m.Name == "BuildRenderTree"
            && m.ContainingType is { } owner
            && RazorSgHelpers.InheritsComponentBase(owner))
            return true;

        return false;
    }

    /// <summary>
    /// Detects whether a type's source location is inside a Razor SG-generated file
    /// (path ends in _razor.g.cs). Used to scope _Imports filtering safely.
    /// </summary>
    private static bool IsRazorGeneratedSource(INamedTypeSymbol type)
    {
        foreach (var location in type.Locations)
        {
            if (!location.IsInSource) continue;
            var path = location.SourceTree?.FilePath;
            if (path is null) continue;
            if (path.EndsWith("_razor.g.cs", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static SymbolCard? BuildCard(ISymbol symbol, Compilation compilation,
        string projectName, INamedTypeSymbol? containingType, string solutionDir)
    {
        // Require a source location
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return null;

        var symbolIdStr = symbol.GetDocumentationCommentId()
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var fqName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var kind = SymbolKindMapper.Map(symbol);
        var signature = SignatureFormatter.Format(symbol);
        var documentation = DocumentationReader.GetSummary(symbol);
        var namespaceName = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var containingTypeName = containingType?.ToDisplayString(
            SymbolDisplayFormat.MinimallyQualifiedFormat);

        var filePathNullable = ToRepoRelativeFilePath(location.SourceTree!.FilePath, solutionDir);
        if (filePathNullable is null) return null;
        FilePath filePath = filePathNullable.Value;

        // Roslyn's primary Location for named symbols (types AND members) points to the
        // identifier token, not the full declaration node. Use the declaring syntax reference
        // to get the full span from keyword/modifier to closing brace/semicolon.
        // VB.NET: DeclaringSyntaxReferences[0] points to the header statement
        // (e.g. FunctionStatementSyntax), not the full MethodBlockSyntax. Walk to
        // the parent when it starts on the same line but ends later (encompasses the body).
        SyntaxNode? declSyntax = symbol.DeclaringSyntaxReferences.Length > 0
            ? symbol.DeclaringSyntaxReferences[0].GetSyntax()
            : null;
        if (declSyntax?.Parent is { } blockParent)
        {
            var childLineSpan = declSyntax.GetLocation().GetLineSpan();
            var parentLineSpan = blockParent.GetLocation().GetLineSpan();
            if (parentLineSpan.StartLinePosition == childLineSpan.StartLinePosition
                && parentLineSpan.EndLinePosition > childLineSpan.EndLinePosition)
            {
                declSyntax = blockParent;
            }
        }
        var lineSpan = declSyntax is not null
            ? declSyntax.GetLocation().GetLineSpan()
            : location.GetLineSpan();
        int spanStart = lineSpan.StartLinePosition.Line + 1;
        int spanEnd = lineSpan.EndLinePosition.Line + 1;

        var visibility = MapVisibility(symbol.DeclaredAccessibility);
        var thrownExceptions = ExtractThrownExceptions(symbol);

        var evidence = new List<EvidencePointer>
        {
            new(
                repoId: RepoId.From("sample"),  // placeholder — set by caller in production
                filePath: filePath,
                lineStart: spanStart,
                lineEnd: spanEnd
            )
        };

        return new SymbolCard(
            SymbolId: SymbolId.From(symbolIdStr),
            FullyQualifiedName: fqName,
            Kind: kind,
            Signature: signature,
            Documentation: documentation,
            Namespace: namespaceName,
            ContainingType: containingTypeName,
            FilePath: filePath,
            SpanStart: spanStart,
            SpanEnd: spanEnd,
            Visibility: visibility,
            CallsTop: [],
            Facts: [],
            SideEffects: [],
            ThrownExceptions: thrownExceptions,
            Evidence: evidence,
            Confidence: Confidence.High
        );
    }

    private static FilePath? ToRepoRelativeFilePath(string absolutePath, string solutionDir = "")
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        string normalized = absolutePath.Replace('\\', '/');

        // Use full path if it doesn't look absolute (i.e., already relative — in-memory tests)
        if (!Path.IsPathRooted(absolutePath))
            return FilePath.From(normalized.TrimStart('/'));

        // Relativize against the solution directory when available (matches ExtractFiles)
        if (!string.IsNullOrEmpty(solutionDir))
        {
            string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';
            if (normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                return FilePath.From(normalized[normalizedDir.Length..]);
        }

        // Fallback: filename only
        string fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return FilePath.From(fileName);
    }

    private static string MapVisibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private => "private",
        _ => "internal",
    };

    private static IReadOnlyList<string> ExtractThrownExceptions(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method) return [];

        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();

            foreach (var throwStmt in syntax.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression is ObjectCreationExpressionSyntax creation)
                    exceptions.Add(creation.Type.ToString());
                else if (throwStmt.Expression is ImplicitObjectCreationExpressionSyntax implicit_)
                    exceptions.Add("Exception"); // can't infer type without semantic model here
            }

            foreach (var throwExpr in syntax.DescendantNodes().OfType<ThrowExpressionSyntax>())
            {
                if (throwExpr.Expression is ObjectCreationExpressionSyntax creation)
                    exceptions.Add(creation.Type.ToString());
            }
        }
        return [.. exceptions];
    }

    internal static string ComputeFileId(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
