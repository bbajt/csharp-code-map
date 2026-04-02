namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="ConfigKeyExtractor"/>.
/// Detects IConfiguration key access in VB.NET syntax trees using four patterns:
///   1. IConfiguration indexer:  _config("key")  [VB uses InvocationExpression for indexers]
///   2. GetValue(Of T)("key")
///   3. GetSection("key")
///   4. Configure(Of T)(config.GetSection("key"))
/// </summary>
internal static class VbConfigKeyExtractor
{
    /// <summary>
    /// Extracts configuration key facts from all VB.NET syntax trees in the compilation.
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

            // Pre-collect GetSection invocations that are arguments to Configure calls.
            var getSectionsInConfigureCalls = CollectGetSectionsInConfigureCalls(root);

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? key = null;
                string? pattern = null;

                if (invocation.Expression is MemberAccessExpressionSyntax ma)
                {
                    var methodName = ma.Name.Identifier.Text;

                    // Pattern 2: GetValue(Of T)("key")
                    if (methodName == "GetValue" &&
                        IsConfigurationAccess(ma.Expression, semanticModel))
                    {
                        key = ExtractFirstStringArg(invocation, semanticModel);
                        pattern = "GetValue";
                    }
                    // Pattern 3: GetSection("key") — skip those used as Configure args (pattern 4)
                    else if (methodName == "GetSection" &&
                        IsConfigurationAccess(ma.Expression, semanticModel) &&
                        !getSectionsInConfigureCalls.Contains(invocation))
                    {
                        key = ExtractFirstStringArg(invocation, semanticModel);
                        pattern = "GetSection";
                    }
                    // Pattern 4: Configure(Of T)(config.GetSection("key"))
                    else if (methodName == "Configure" &&
                        TryGetGetSectionKey(invocation, semanticModel, out var sectionKey))
                    {
                        key = sectionKey;
                        pattern = "Options Configure";
                    }
                }
                else if (IsIConfigurationIndexer(invocation, semanticModel))
                {
                    // Pattern 1: _config("key") — VB indexer is InvocationExpression
                    key = ExtractFirstStringArg(invocation, semanticModel);
                    pattern = "IConfiguration indexer";
                }

                if (key is null || pattern is null) continue;

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
                    Kind: FactKind.Config,
                    Value: $"{key}|{pattern}",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }
        }

        return facts;
    }

    // ── Receiver / indexer type detection ────────────────────────────────────

    private static bool IsConfigurationAccess(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is null) return false;
        var typeInfo = semanticModel.GetTypeInfo(expression);
        var typeName = typeInfo.Type?.ToDisplayString() ?? "";
        return typeName.Contains("IConfiguration");
    }

    /// <summary>
    /// In VB.NET, array/indexer access uses the same <c>()</c> syntax as method calls.
    /// Detects <c>_config("key")</c> where the expression (not a member access) resolves
    /// to an IConfiguration type.
    /// </summary>
    private static bool IsIConfigurationIndexer(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax)
            return false;
        if (invocation.Expression is null) return false;
        var typeInfo = semanticModel.GetTypeInfo(invocation.Expression);
        var typeName = typeInfo.Type?.ToDisplayString() ?? "";
        return typeName.Contains("IConfiguration");
    }

    // ── Configure(Of T)(GetSection) helpers ─────────────────────────────────

    private static HashSet<InvocationExpressionSyntax> CollectGetSectionsInConfigureCalls(
        SyntaxNode root)
    {
        var result = new HashSet<InvocationExpressionSyntax>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Name.Identifier.Text != "Configure") continue;

            foreach (var arg in invocation.ArgumentList.Arguments.OfType<SimpleArgumentSyntax>())
            {
                if (arg.Expression is InvocationExpressionSyntax inner &&
                    inner.Expression is MemberAccessExpressionSyntax innerMa &&
                    innerMa.Name.Identifier.Text == "GetSection")
                {
                    result.Add(inner);
                }
            }
        }
        return result;
    }

    private static bool TryGetGetSectionKey(
        InvocationExpressionSyntax configureInvocation,
        SemanticModel semanticModel,
        out string? key)
    {
        key = null;
        foreach (var arg in configureInvocation.ArgumentList.Arguments.OfType<SimpleArgumentSyntax>())
        {
            if (arg.Expression is InvocationExpressionSyntax inner &&
                inner.Expression is MemberAccessExpressionSyntax innerMa &&
                innerMa.Name.Identifier.Text == "GetSection")
            {
                key = ExtractFirstStringArg(inner, semanticModel);
                return key is not null;
            }
        }
        return false;
    }

    // ── String argument extraction ────────────────────────────────────────────

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
