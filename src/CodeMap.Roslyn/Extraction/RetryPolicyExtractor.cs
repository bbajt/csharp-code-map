namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract resilience/retry policy facts.
/// Uses method-name-based detection (Confidence.Medium) for:
///   Polly:      RetryAsync, WaitAndRetryAsync, CircuitBreakerAsync, AddTransientHttpErrorPolicy
///   Resilience: AddRetry, AddCircuitBreaker, AddResilienceHandler
/// </summary>
internal static class RetryPolicyExtractor
{
    /// <summary>
    /// Extracts retry/resilience policy facts from all syntax trees in the compilation.
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

            foreach (var invocation in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                var methodName = memberAccess.Name.Identifier.Text;

                var (description, framework) = methodName switch
                {
                    "RetryAsync" => (ExtractRetryDescription(invocation), "Polly"),
                    "WaitAndRetryAsync" => (ExtractWaitAndRetryDescription(invocation), "Polly"),
                    "CircuitBreakerAsync" => ("CircuitBreaker", "Polly"),
                    "AddRetry" => (ExtractAddRetryDescription(invocation), "Resilience"),
                    "AddCircuitBreaker" => ("CircuitBreaker", "Resilience"),
                    "AddResilienceHandler" => ("ResilienceHandler", "Resilience"),
                    "AddTransientHttpErrorPolicy" => ("TransientHttpErrorPolicy", "Polly"),
                    _ => (null, null)
                };

                if (description is null) continue;

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
                    Kind: FactKind.RetryPolicy,
                    Value: $"{description}|{framework}",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.Medium));
            }
        }

        return facts;
    }

    // ── Description builders ──────────────────────────────────────────────────

    private static string ExtractRetryDescription(InvocationExpressionSyntax invocation)
    {
        var retryCount = ExtractFirstIntArg(invocation);
        return retryCount is not null ? $"RetryAsync({retryCount})" : "RetryAsync";
    }

    private static string ExtractWaitAndRetryDescription(InvocationExpressionSyntax invocation)
    {
        var retryCount = ExtractFirstIntArg(invocation);
        return retryCount is not null ? $"WaitAndRetryAsync({retryCount})" : "WaitAndRetryAsync";
    }

    private static string ExtractAddRetryDescription(InvocationExpressionSyntax invocation)
    {
        // Complex to extract MaxRetryAttempts from named arguments or object init — fall back to method name
        return "AddRetry";
    }

    private static int? ExtractFirstIntArg(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
                literal.Token.Value is int count)
                return count;
        }
        return null;
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
