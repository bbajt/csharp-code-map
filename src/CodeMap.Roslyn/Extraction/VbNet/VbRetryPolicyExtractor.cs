namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="RetryPolicyExtractor"/>.
/// Name-based detection for Polly and Microsoft.Extensions.Resilience retry/circuit-breaker calls.
/// Value format: "framework|description" (e.g. "Polly|retry:3").
/// </summary>
internal static class VbRetryPolicyExtractor
{
    /// <summary>
    /// Extracts retry/resilience policy facts from all VB.NET syntax trees in the compilation.
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

                var (description, framework) = methodName switch
                {
                    "RetryAsync" => (BuildRetryDescription(invocation), "Polly"),
                    "WaitAndRetryAsync" => (BuildWaitAndRetryDescription(invocation), "Polly"),
                    "CircuitBreakerAsync" => ("circuit-breaker", "Polly"),
                    "AddRetry" => ("retry", "Resilience"),
                    "AddCircuitBreaker" => ("circuit-breaker", "Resilience"),
                    "AddResilienceHandler" => ("resilience-handler", "Resilience"),
                    "AddTransientHttpErrorPolicy" => ("transient-http-error", "Polly"),
                    _ => (null, null)
                };

                if (description is null) continue;

                var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                var symbolIdStr = containingSymbol is not null
                    ? GetSymbolId(containingSymbol) : null;

                StableId stableId = default;
                if (symbolIdStr is not null) stableIdMap?.TryGetValue(symbolIdStr, out stableId);

                var lineSpan = invocation.GetLocation().GetLineSpan();

                facts.Add(new ExtractedFact(
                    SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
                    StableId: stableId == default ? null : stableId,
                    Kind: FactKind.RetryPolicy,
                    Value: $"{framework}|{description}",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.Medium));
            }
        }

        return facts;
    }

    // ── Description builders ──────────────────────────────────────────────────

    private static string BuildRetryDescription(InvocationExpressionSyntax invocation)
    {
        var count = ExtractFirstIntArg(invocation);
        return count is not null ? $"retry:{count}" : "retry";
    }

    private static string BuildWaitAndRetryDescription(InvocationExpressionSyntax invocation)
    {
        var count = ExtractFirstIntArg(invocation);
        return count is not null ? $"wait-and-retry:{count}" : "wait-and-retry";
    }

    private static int? ExtractFirstIntArg(InvocationExpressionSyntax invocation)
    {
        var firstArg = invocation.ArgumentList.Arguments
            .OfType<SimpleArgumentSyntax>().FirstOrDefault();
        if (firstArg?.Expression is LiteralExpressionSyntax lit &&
            lit.Token.Value is int count)
            return count;
        return null;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

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
