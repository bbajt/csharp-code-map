namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract ASP.NET middleware pipeline facts.
/// Detects Use* and Map* calls on WebApplication/IApplicationBuilder receivers.
/// Tracks sequential pipeline position per method body.
/// MapGet/MapPost/MapPut/MapDelete/MapPatch are skipped (captured by EndpointExtractor).
/// MapControllers/MapRazorPages/etc. are marked as terminal middleware.
/// </summary>
internal static class MiddlewareExtractor
{
    private static readonly HashSet<string> EndpointMapMethods = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"
    };

    /// <summary>
    /// Extracts middleware pipeline facts from all syntax trees in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var facts = new List<ExtractedFact>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath is null or "") continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            // Process each method body independently — position resets per method
            foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                int position = 0;

                foreach (var invocation in methodDecl.DescendantNodes()
                             .OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                        continue;

                    var methodName = memberAccess.Name.Identifier.Text;

                    // Skip endpoint-style Map methods (EndpointExtractor handles those)
                    if (EndpointMapMethods.Contains(methodName))
                        continue;

                    bool isUse = methodName.StartsWith("Use", StringComparison.Ordinal);
                    bool isMapBased = methodName.StartsWith("Map", StringComparison.Ordinal);

                    if (!isUse && !isMapBased)
                        continue;

                    // For Map* calls: require semantic model confirmation of app builder receiver
                    if (isMapBased && !IsAppBuilderReceiver(memberAccess.Expression, semanticModel))
                        continue;

                    // For Use* calls: try semantic model first, then name-based text fallback
                    if (isUse &&
                        !IsAppBuilderReceiver(memberAccess.Expression, semanticModel) &&
                        !LooksLikeAppBuilder(memberAccess.Expression))
                        continue;

                    position++;

                    // Map* calls are terminal middleware (pipeline short-circuits)
                    bool isTerminal = isMapBased;
                    string tag = isTerminal ? "|terminal" : "";
                    string value = $"{methodName}|pos:{position}{tag}";

                    var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                    var symbolIdStr = containingSymbol is not null
                        ? GetSymbolId(containingSymbol) : null;

                    StableId stableId = default;
                    if (symbolIdStr is not null)
                        stableIdMap?.TryGetValue(symbolIdStr, out stableId);

                    var lineSpan = invocation.GetLocation().GetLineSpan();

                    facts.Add(new ExtractedFact(
                        SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
                        StableId: stableId == default ? null : stableId,
                        Kind: FactKind.Middleware,
                        Value: value,
                        FilePath: filePath,
                        LineStart: lineSpan.StartLinePosition.Line + 1,
                        LineEnd: lineSpan.EndLinePosition.Line + 1,
                        Confidence: Confidence.High));
                }
            }
        }

        return facts;
    }

    // ── Receiver type detection ───────────────────────────────────────────────

    private static bool IsAppBuilderReceiver(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(expression);
        var typeName = typeInfo.Type?.ToDisplayString() ?? "";
        return typeName.Contains("WebApplication")
            || typeName.Contains("IApplicationBuilder")
            || typeName.Contains("IEndpointRouteBuilder");
    }

    private static bool LooksLikeAppBuilder(ExpressionSyntax expression)
    {
        var text = expression.ToString().ToLowerInvariant();
        return text == "app"
            || text.Contains("application")
            || text.Contains("builder")
            || text.Contains("appbuilder");
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static ISymbol? FindContainingSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is MethodDeclarationSyntax method)
                return semanticModel.GetDeclaredSymbol(method);
            if (current is LocalFunctionStatementSyntax local)
                return semanticModel.GetDeclaredSymbol(local);
            current = current.Parent;
        }
        return null;
    }

    private static string GetSymbolId(ISymbol symbol)
        => symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString();

    private static FilePath MakeRepoRelative(string filePath, string normalizedDir)
    {
        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            return FilePath.From(normalized[normalizedDir.Length..]);
        return FilePath.From(Path.GetFileName(normalized));
    }
}
