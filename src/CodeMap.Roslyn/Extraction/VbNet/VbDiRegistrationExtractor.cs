namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="DiRegistrationExtractor"/>.
/// Detects DI registration calls (AddScoped, AddSingleton, etc.) in VB.NET syntax trees.
/// Supports the same 7 patterns as the C# extractor, adapted for VB.NET generic syntax.
/// </summary>
internal static class VbDiRegistrationExtractor
{
    /// <summary>
    /// Extracts DI registration facts from all VB.NET syntax trees in the compilation.
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

            foreach (var invocation in syntaxTree.GetRoot()
                .DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                var methodName = memberAccess.Name.Identifier.Text;

                var lifetime = methodName switch
                {
                    "AddScoped" or "TryAddScoped" => "Scoped",
                    "AddSingleton" or "TryAddSingleton" => "Singleton",
                    "AddTransient" or "TryAddTransient" => "Transient",
                    "AddHostedService" => "Singleton",
                    _ => null
                };

                if (lifetime is null) continue;

                if (!IsServiceCollectionReceiver(memberAccess.Expression, semanticModel) &&
                    !LooksLikeServiceCollection(memberAccess.Expression))
                    continue;

                var typeArgs = GetGenericTypeArguments(memberAccess.Name, semanticModel);

                string serviceType, implType;

                if (typeArgs.Count == 2)
                {
                    // Pattern 1/4: AddScoped(Of IService, Impl)() or TryAdd variant
                    serviceType = typeArgs[0];
                    implType = typeArgs[1];
                }
                else if (typeArgs.Count == 1 && HasLambdaArgument(invocation))
                {
                    // Pattern 3: AddScoped(Of IService)(Function(sp) ...) — factory
                    serviceType = typeArgs[0];
                    implType = "factory";
                }
                else if (typeArgs.Count == 1)
                {
                    serviceType = typeArgs[0];
                    var instanceType = TryGetInstanceArgType(invocation, semanticModel);
                    implType = (instanceType is not null && instanceType != serviceType)
                        ? instanceType
                        : serviceType;
                    if (methodName == "AddHostedService")
                        lifetime = "Singleton|HostedService";
                }
                else if (typeArgs.Count == 0 && HasLambdaArgument(invocation))
                {
                    // Pattern 7: AddSingleton(Function(sp) New Impl()) — inferred type arg
                    var resolved = GetResolvedTypeArguments(invocation, semanticModel);
                    serviceType = resolved.Count == 1 ? resolved[0] : "unknown";
                    implType = "factory";
                }
                else
                {
                    continue;
                }

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
                    Kind: FactKind.DiRegistration,
                    Value: $"{serviceType} \u2192 {implType}|{lifetime}",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }
        }

        return facts;
    }

    // ── Receiver type detection ───────────────────────────────────────────────

    private static bool IsServiceCollectionReceiver(
        ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is null) return false;
        var typeInfo = semanticModel.GetTypeInfo(expression);
        var typeName = typeInfo.Type?.ToDisplayString() ?? "";
        return typeName.Contains("IServiceCollection") || typeName.Contains("ServiceCollection");
    }

    private static bool LooksLikeServiceCollection(ExpressionSyntax expression)
    {
        var text = expression.ToString().ToLowerInvariant();
        return text.Contains("services") || text.Contains("servicecollection");
    }

    // ── Type argument extraction ──────────────────────────────────────────────

    private static List<string> GetGenericTypeArguments(
        SimpleNameSyntax nameSyntax, SemanticModel semanticModel)
    {
        if (nameSyntax is not GenericNameSyntax generic)
            return [];

        var result = new List<string>();
        foreach (var arg in generic.TypeArgumentList.Arguments)
        {
            var typeInfo = semanticModel.GetTypeInfo(arg);
            if (typeInfo.Type is not null)
                result.Add(typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
        return result;
    }

    private static bool HasLambdaArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments
            .OfType<SimpleArgumentSyntax>()
            .Any(a => a.Expression is LambdaExpressionSyntax);

    private static string? TryGetInstanceArgType(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var arg = invocation.ArgumentList.Arguments
            .OfType<SimpleArgumentSyntax>()
            .FirstOrDefault(a => a.Expression is not LambdaExpressionSyntax);
        if (arg is null || arg.Expression is null) return null;
        var type = semanticModel.GetTypeInfo(arg.Expression).Type;
        return type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static List<string> GetResolvedTypeArguments(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && method.TypeArguments.Length > 0)
        {
            return method.TypeArguments
                .Select(t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                .ToList();
        }
        return [];
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
