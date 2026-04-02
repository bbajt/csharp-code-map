namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="LogExtractor"/>.
/// Detects ILogger.Log* call patterns in VB.NET syntax trees.
/// Value format: "LogLevel|message template" (level first, to match VB convention).
/// </summary>
internal static class VbLogExtractor
{
    private static readonly HashSet<string> LogLevelMethods = new(StringComparer.Ordinal)
    {
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical"
    };

    /// <summary>
    /// Extracts log call facts from all VB.NET syntax trees in the compilation.
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
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;

                var methodName = memberAccess.Name.Identifier.Text;
                if (!LogLevelMethods.Contains(methodName)) continue;

                if (!IsLoggerReceiver(memberAccess.Expression, semanticModel)) continue;

                var messageTemplate = ExtractFirstStringArg(invocation, semanticModel);
                if (messageTemplate is null) continue;

                var logLevel = methodName["Log".Length..]; // "LogWarning" → "Warning"

                var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                var symbolIdStr = containingSymbol is not null ? GetSymbolId(containingSymbol) : null;

                StableId stableId = default;
                if (symbolIdStr is not null) stableIdMap?.TryGetValue(symbolIdStr, out stableId);

                var lineSpan = invocation.GetLocation().GetLineSpan();

                facts.Add(new ExtractedFact(
                    SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
                    StableId: stableId == default ? null : stableId,
                    Kind: FactKind.Log,
                    Value: $"{logLevel}|{messageTemplate}",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }
        }

        return facts;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsLoggerReceiver(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is null) return false;
        var typeName = semanticModel.GetTypeInfo(expression).Type?.ToDisplayString() ?? "";
        if (typeName.Contains("ILogger")) return true;
        if (expression is IdentifierNameSyntax id)
            return id.Identifier.Text.Contains("logger", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string? ExtractFirstStringArg(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var firstArg = invocation.ArgumentList.Arguments
            .OfType<SimpleArgumentSyntax>().FirstOrDefault();
        if (firstArg is null) return null;
        var cv = semanticModel.GetConstantValue(firstArg.Expression);
        if (cv.HasValue && cv.Value is string s) return s;
        if (firstArg.Expression is LiteralExpressionSyntax lit && lit.Token.Value is string ls)
            return ls;
        return null;
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
