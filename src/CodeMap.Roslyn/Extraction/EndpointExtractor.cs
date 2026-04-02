namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract ASP.NET HTTP endpoint facts.
/// Supports controller-based routing ([HttpGet], [HttpPost], etc.) and
/// minimal API routing (app.MapGet, app.MapPost, etc.).
/// Uses semantic model when available (Confidence.High) and falls back
/// to syntactic detection when compilation is unavailable.
/// </summary>
internal static class EndpointExtractor
{
    private static readonly HashSet<string> HttpMethodAttributes = new(StringComparer.Ordinal)
    {
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute",
        "HttpDeleteAttribute", "HttpPatchAttribute",
    };

    private static readonly HashSet<string> MapMethodNames = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch",
    };

    /// <summary>
    /// Extracts endpoint facts from all syntax trees in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        if (compilation.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            return VbNet.VbEndpointExtractor.ExtractAll(compilation, solutionDir, stableIdMap);
        var facts = new List<ExtractedFact>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath is null or "") continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            // Controller-based endpoints
            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                if (classSymbol is null) continue;

                var routePrefix = ExtractRoutePrefix(classSymbol);
                if (routePrefix is null && !HasApiControllerAttribute(classSymbol))
                    continue;

                foreach (var methodDecl in classDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                    if (methodSymbol is null) continue;

                    var (httpMethod, routeTemplate) = ExtractHttpMethodAndRoute(methodSymbol);
                    if (httpMethod is null) continue;

                    var fullRoute = CombineRoutes(routePrefix, routeTemplate ?? "");
                    fullRoute = ResolveControllerToken(fullRoute, classSymbol.Name);

                    var methodId = GetSymbolId(methodSymbol);
                    StableId stableId = default;
                    stableIdMap?.TryGetValue(methodId, out stableId);

                    var lineSpan = methodDecl.GetLocation().GetLineSpan();
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
                var routeArg = ExtractFirstStringArg(invocation);
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

    private static string? ExtractFirstStringArg(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return null;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        if (firstArg is LiteralExpressionSyntax lit &&
            lit.Token.Value is string s)
        {
            return s;
        }
        return null;
    }

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

    // ── Shared helpers ────────────────────────────────────────────────────────

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
