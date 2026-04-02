namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract DI registration facts.
/// Supports seven detection patterns:
///   1. Generic pair:        services.AddScoped&lt;IService, Impl&gt;()
///   2. Self-registration:   services.AddSingleton&lt;Service&gt;()
///   3. Factory lambda:      services.AddScoped&lt;IService&gt;(sp => new Impl())
///   4. TryAdd variants:     services.TryAddScoped&lt;IService, Impl&gt;()
///   5. AddHostedService:    services.AddHostedService&lt;Worker&gt;()
///   6. Instance argument:   services.AddSingleton&lt;IService&gt;(new Impl(...))
///   7. Inferred-type factory: services.AddSingleton(sp => new Impl(...))
/// Uses semantic model to verify receiver type is IServiceCollection;
/// falls back to name-based check for stubs and generated code.
/// </summary>
internal static class DiRegistrationExtractor
{
    /// <summary>
    /// Extracts DI registration facts from all syntax trees in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        if (compilation.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            return VbNet.VbDiRegistrationExtractor.ExtractAll(compilation, solutionDir, stableIdMap);
        var facts = new List<ExtractedFact>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath is null or "") continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
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

                // Verify receiver is IServiceCollection (semantic) or looks like one (text fallback)
                if (!IsServiceCollectionReceiver(memberAccess.Expression, semanticModel) &&
                    !LooksLikeServiceCollection(memberAccess.Expression))
                    continue;

                // Extract generic type arguments
                var typeArgs = GetGenericTypeArguments(memberAccess.Name, semanticModel);

                string serviceType, implType;

                if (typeArgs.Count == 2)
                {
                    // Pattern 1/4: AddScoped<IService, Impl>() or TryAdd variant
                    serviceType = typeArgs[0];
                    implType = typeArgs[1];
                }
                else if (typeArgs.Count == 1 && HasLambdaArgument(invocation))
                {
                    // Pattern 3: AddScoped<IService>(sp => ...) — factory registration
                    serviceType = typeArgs[0];
                    implType = "factory";
                }
                else if (typeArgs.Count == 1)
                {
                    serviceType = typeArgs[0];
                    if (HasLambdaArgument(invocation))
                    {
                        // Pattern 3 (already handled above as typeArgs.Count==1 && HasLambda)
                        // but guard here too for safety
                        implType = "factory";
                    }
                    else
                    {
                        // Pattern 6: AddSingleton<IService>(new Impl(...)) — instance argument
                        // Resolve the concrete type from the argument if it differs from the service type.
                        var instanceType = TryGetInstanceArgType(invocation, semanticModel);
                        implType = (instanceType is not null && instanceType != serviceType)
                            ? instanceType
                            : serviceType;
                    }
                    if (methodName == "AddHostedService")
                        lifetime = "Singleton|HostedService";
                }
                else if (typeArgs.Count == 0 && HasLambdaArgument(invocation))
                {
                    // Pattern 7: AddSingleton(sp => new Impl(...)) — type arg inferred by compiler.
                    // Ask the bound method symbol for the resolved type argument rather than relying
                    // on syntax-level GenericNameSyntax (which is absent when the type is inferred).
                    var resolved = GetResolvedTypeArguments(invocation, semanticModel);
                    serviceType = resolved.Count == 1 ? resolved[0] : "unknown";
                    implType = "factory";
                }
                else
                {
                    continue; // cannot determine types
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

    private static bool IsServiceCollectionReceiver(ExpressionSyntax expression, SemanticModel semanticModel)
    {
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

    private static List<string> GetGenericTypeArguments(SimpleNameSyntax nameSyntax, SemanticModel semanticModel)
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
            .Any(a => a.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);

    /// <summary>
    /// Returns the concrete type name of the first non-lambda argument, or <c>null</c> if there
    /// is no such argument or its type cannot be resolved.
    /// Handles Pattern 6: <c>AddSingleton&lt;IFoo&gt;(new FooImpl(...))</c>.
    /// </summary>
    private static string? TryGetInstanceArgType(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var arg = invocation.ArgumentList.Arguments.FirstOrDefault(
            a => a.Expression is not LambdaExpressionSyntax
                              and not AnonymousMethodExpressionSyntax);
        if (arg is null) return null;
        var type = semanticModel.GetTypeInfo(arg.Expression).Type;
        return type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    /// <summary>
    /// Returns the resolved generic type arguments from the bound method symbol.
    /// Used for Pattern 7 where the compiler infers the type arg from the lambda return type
    /// (e.g. <c>AddSingleton(sp => new FooImpl())</c> has no syntax-level type argument
    /// but Roslyn resolves <c>TService = FooImpl</c> on the method symbol).
    /// </summary>
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
