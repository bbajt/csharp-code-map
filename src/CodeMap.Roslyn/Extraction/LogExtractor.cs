namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract structured log statement facts (<see cref="FactKind.Log"/>).
/// Detects ILogger extension method calls (LogTrace/Debug/Information/Warning/Error/Critical)
/// and the generic Log(LogLevel, ...) overload.
/// Message templates are extracted from string literal arguments; dynamic messages are skipped.
/// </summary>
internal static class LogExtractor
{
    private static readonly HashSet<string> LogLevelMethods = new(StringComparer.Ordinal)
    {
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical"
    };

    /// <summary>
    /// Extracts log call facts from all syntax trees in the compilation.
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

                string? logLevel = LogLevelMethods.Contains(methodName)
                    ? methodName["Log".Length..] // strip "Log" prefix: LogWarning → "Warning"
                    : methodName == "Log"
                        ? ExtractLogLevelFromArg(invocation)
                        : null;

                if (logLevel is null) continue;

                if (!IsLoggerReceiver(memberAccess.Expression, semanticModel)) continue;

                var messageTemplate = ExtractMessageTemplate(invocation, methodName, semanticModel);
                if (messageTemplate is null) continue;

                var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                var symbolIdStr = containingSymbol is not null ? GetSymbolId(containingSymbol) : null;

                StableId stableId = default;
                if (symbolIdStr is not null)
                    stableIdMap?.TryGetValue(symbolIdStr, out stableId);

                var lineSpan = invocation.GetLocation().GetLineSpan();

                facts.Add(new ExtractedFact(
                    SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
                    StableId: stableId == default ? null : stableId,
                    Kind: FactKind.Log,
                    Value: $"{messageTemplate}|{logLevel}",
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
        var typeInfo = semanticModel.GetTypeInfo(expression);
        var typeName = typeInfo.Type?.ToDisplayString() ?? "";
        if (typeName.Contains("ILogger"))
            return true;

        // Fallback: identifier name contains "logger" (e.g. _logger, logger)
        if (expression is IdentifierNameSyntax id)
            return id.Identifier.Text.Contains("logger", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static string? ExtractMessageTemplate(
        InvocationExpressionSyntax invocation,
        string methodName,
        SemanticModel semanticModel)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0) return null;

        // For Log(LogLevel, ...) the message is the 2nd argument (index 1)
        int startIndex = methodName == "Log" ? 1 : 0;

        // For LogError / LogCritical, if the first arg is an Exception, skip it
        if ((methodName == "LogError" || methodName == "LogCritical") && args.Count > 1)
        {
            var firstType = semanticModel.GetTypeInfo(args[0].Expression).Type;
            if (firstType is not null && InheritsFromException(firstType))
                startIndex = 1;
        }

        if (startIndex >= args.Count) return null;

        var arg = args[startIndex].Expression;

        // Prefer constant value (covers string literals and const fields)
        var constant = semanticModel.GetConstantValue(arg);
        if (constant.HasValue && constant.Value is string s)
            return s;

        // Syntax-level string literal fallback
        if (arg is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;

        // Interpolated string — store as-is (keeps structured placeholders visible)
        if (arg is InterpolatedStringExpressionSyntax interpolated)
            return interpolated.ToString();

        // Dynamic message — cannot extract a static template
        return null;
    }

    private static string? ExtractLogLevelFromArg(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0) return null;

        // Expect: LogLevel.Warning, LogLevel.Error, etc.
        if (args[0].Expression is MemberAccessExpressionSyntax ma)
            return ma.Name.Identifier.Text;

        return "Unknown";
    }

    private static bool InheritsFromException(ITypeSymbol type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.Name == "Exception") return true;
            current = current.BaseType;
        }
        return false;
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
