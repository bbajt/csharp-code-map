namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="MiddlewareExtractor"/>.
/// Detects Use*/Map* calls on IApplicationBuilder receivers in VB.NET.
/// Position counter resets per MethodBlockSyntax body.
/// </summary>
internal static class VbMiddlewareExtractor
{
    private static readonly HashSet<string> EndpointMapMethods = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"
    };

    /// <summary>
    /// Extracts middleware pipeline facts from all VB.NET syntax trees in the compilation.
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
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            // Process each VB method body independently — position resets per method
            foreach (var methodBlock in syntaxTree.GetRoot()
                .DescendantNodes().OfType<MethodBlockSyntax>())
            {
                int position = 0;

                foreach (var invocation in methodBlock.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                        continue;

                    var methodName = memberAccess.Name.Identifier.Text;
                    if (EndpointMapMethods.Contains(methodName)) continue;

                    bool isUse = methodName.StartsWith("Use", StringComparison.Ordinal);
                    bool isMapBased = methodName.StartsWith("Map", StringComparison.Ordinal);

                    if (!isUse && !isMapBased) continue;

                    if (isMapBased && !IsAppBuilderReceiver(memberAccess.Expression, semanticModel))
                        continue;

                    if (isUse &&
                        !IsAppBuilderReceiver(memberAccess.Expression, semanticModel) &&
                        !LooksLikeAppBuilder(memberAccess.Expression))
                        continue;

                    position++;

                    bool isTerminal = isMapBased;
                    string tag = isTerminal ? "|terminal" : "";
                    string value = $"{methodName}|pos:{position}{tag}";

                    var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                    var symbolIdStr = containingSymbol is not null
                        ? GetSymbolId(containingSymbol) : null;

                    StableId stableId = default;
                    if (symbolIdStr is not null) stableIdMap?.TryGetValue(symbolIdStr, out stableId);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsAppBuilderReceiver(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is null) return false;
        var typeName = semanticModel.GetTypeInfo(expression).Type?.ToDisplayString() ?? "";
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

    private static ISymbol? FindContainingSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var current = node.Parent;
        while (current is not null)
        {
            var declared = semanticModel.GetDeclaredSymbol(current);
            if (declared is IMethodSymbol) return declared;
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
