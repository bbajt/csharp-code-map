namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="EndpointExtractor"/>.
/// Walks VB.NET SyntaxTrees to extract ASP.NET HTTP endpoint facts using
/// controller-based routing ([HttpGet], [HttpPost], etc.) and
/// minimal API routing (app.MapGet, app.MapPost, etc.).
/// </summary>
internal static class VbEndpointExtractor
{
    private static readonly HashSet<string> HttpAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpGetAttribute",
        "HttpPost", "HttpPostAttribute",
        "HttpPut", "HttpPutAttribute",
        "HttpDelete", "HttpDeleteAttribute",
        "HttpPatch", "HttpPatchAttribute",
    };

    private static readonly HashSet<string> MapMethodNames = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch",
    };

    /// <summary>
    /// Extracts endpoint facts from all VB.NET syntax trees in the compilation.
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

            // Controller-based endpoints
            foreach (var classBlock in root.DescendantNodes().OfType<ClassBlockSyntax>())
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classBlock) as INamedTypeSymbol;
                if (classSymbol is null) continue;

                var routePrefix = ExtractRoutePrefix(classSymbol);
                if (routePrefix is null && !HasApiControllerAttribute(classSymbol))
                    continue;

                foreach (var methodBlock in classBlock.Members.OfType<MethodBlockSyntax>())
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(methodBlock) as IMethodSymbol;
                    if (methodSymbol is null) continue;

                    var (httpMethod, routeTemplate) = ExtractHttpMethodAndRoute(methodSymbol);
                    if (httpMethod is null) continue;

                    var fullRoute = CombineRoutes(routePrefix, routeTemplate ?? "");
                    fullRoute = ResolveControllerToken(fullRoute, classSymbol.Name);

                    var methodId = GetSymbolId(methodSymbol);
                    StableId stableId = default;
                    stableIdMap?.TryGetValue(methodId, out stableId);

                    var lineSpan = methodBlock.GetLocation().GetLineSpan();
                    facts.Add(new ExtractedFact(
                        SymbolId: SymbolId.From(methodId),
                        StableId: stableId == default ? null : stableId,
                        Kind: FactKind.Route,
                        Value: $"{httpMethod} {fullRoute}",
                        FilePath: filePath,
                        LineStart: lineSpan.StartLinePosition.Line + 1,
                        LineEnd: lineSpan.EndLinePosition.Line + 1,
                        Confidence: Confidence.High));
                }
            }

            // Minimal API endpoints (app.MapGet, app.MapPost, etc.)
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                var methodName = memberAccess.Name.Identifier.Text;
                if (!MapMethodNames.Contains(methodName)) continue;

                var httpMethod = methodName["Map".Length..].ToUpperInvariant();
                var routeArg = ExtractFirstStringArg(invocation, semanticModel);
                if (routeArg is null) continue;

                var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                var handlerId = containingSymbol is not null
                    ? GetSymbolId(containingSymbol)
                    : "__minimal_api__";

                StableId stableId = default;
                stableIdMap?.TryGetValue(handlerId, out stableId);

                var lineSpan = invocation.GetLocation().GetLineSpan();
                facts.Add(new ExtractedFact(
                    SymbolId: SymbolId.From(handlerId),
                    StableId: stableId == default ? null : stableId,
                    Kind: FactKind.Route,
                    Value: $"{httpMethod} {routeArg}",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }
        }

        return facts;
    }

    // ── Route extraction helpers ──────────────────────────────────────────────

    private static string? ExtractRoutePrefix(INamedTypeSymbol classSymbol)
    {
        foreach (var attr in classSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "RouteAttribute" &&
                attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string template)
            {
                return template;
            }
        }
        return null;
    }

    private static bool HasApiControllerAttribute(INamedTypeSymbol classSymbol)
    {
        foreach (var attr in classSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "ApiControllerAttribute")
                return true;
        }
        return false;
    }

    private static (string? HttpMethod, string? RouteTemplate) ExtractHttpMethodAndRoute(
        IMethodSymbol methodSymbol)
    {
        foreach (var attr in methodSymbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            string? httpMethod = name switch
            {
                "HttpGetAttribute" => "GET",
                "HttpPostAttribute" => "POST",
                "HttpPutAttribute" => "PUT",
                "HttpDeleteAttribute" => "DELETE",
                "HttpPatchAttribute" => "PATCH",
                _ => null,
            };

            if (httpMethod is not null)
            {
                var routeTemplate = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value as string ?? ""
                    : "";
                return (httpMethod, routeTemplate);
            }
        }
        return (null, null);
    }

    private static string CombineRoutes(string? prefix, string template)
    {
        if (prefix is null)
            return "/" + template.TrimStart('/');

        var path = prefix.TrimEnd('/');
        if (!string.IsNullOrEmpty(template))
            path += "/" + template.TrimStart('/');
        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;
        return path;
    }

    private static string ResolveControllerToken(string route, string className)
    {
        var controllerName = className.EndsWith("Controller", StringComparison.Ordinal)
            ? className[..^"Controller".Length]
            : className;
        return route.Replace("[controller]", controllerName.ToLowerInvariant(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Minimal API helpers ───────────────────────────────────────────────────

    private static string? ExtractFirstStringArg(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var firstArg = invocation.ArgumentList.Arguments
            .OfType<SimpleArgumentSyntax>()
            .FirstOrDefault();
        if (firstArg is null) return null;

        var constantValue = semanticModel.GetConstantValue(firstArg.Expression);
        if (constantValue.HasValue && constantValue.Value is string s) return s;
        if (firstArg.Expression is LiteralExpressionSyntax lit &&
            lit.Token.Value is string ls) return ls;
        return null;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static ISymbol? FindContainingSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var current = node.Parent;
        while (current is not null)
        {
            var declared = semanticModel.GetDeclaredSymbol(current);
            if (declared is IMethodSymbol)
                return declared;
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
