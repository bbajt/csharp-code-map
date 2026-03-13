namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract IConfiguration key usage facts.
/// Supports four detection patterns:
///   1. IConfiguration indexer:             _config["key"]
///   2. GetValue&lt;T&gt; call:              _config.GetValue&lt;int&gt;("key")
///   3. GetSection call:                    _config.GetSection("key")
///   4. Options pattern (Configure+Bind):   services.Configure&lt;T&gt;(config.GetSection("key"))
/// Uses semantic model to verify receiver type is IConfiguration/IConfigurationSection.
/// </summary>
internal static class ConfigKeyExtractor
{
    /// <summary>
    /// Extracts configuration key facts from all syntax trees in the compilation.
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

            // Pre-collect GetSection invocations that are arguments to Configure<T> calls.
            // These are handled by pattern 4 and must be skipped in pattern 3 to avoid duplicates.
            var getSectionsInConfigureCalls = CollectGetSectionsInConfigureCalls(root);

            // === Pattern 1: IConfiguration["key"] indexer ===
            foreach (var elementAccess in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (!IsConfigurationAccess(elementAccess.Expression, semanticModel)) continue;

                var key = ExtractBracketStringArg(elementAccess.ArgumentList, semanticModel);
                if (key is null) continue;

                EmitFact(facts, key, "IConfiguration indexer", elementAccess,
                    filePath, semanticModel, stableIdMap);
            }

            // === Patterns 2, 3, 4: member invocations ===
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;
                var methodName = memberAccess.Name.Identifier.Text;

                // Pattern 2: GetValue<T>("key") on IConfiguration
                if (methodName == "GetValue" &&
                    IsConfigurationAccess(memberAccess.Expression, semanticModel))
                {
                    var key = ExtractFirstStringArg(invocation, semanticModel);
                    if (key is null) continue;
                    EmitFact(facts, key, "GetValue", invocation,
                        filePath, semanticModel, stableIdMap);
                    continue;
                }

                // Pattern 3: GetSection("key") on IConfiguration — skip those already
                // captured as Configure<T>(GetSection("key")) arguments (pattern 4)
                if (methodName == "GetSection" &&
                    IsConfigurationAccess(memberAccess.Expression, semanticModel))
                {
                    if (getSectionsInConfigureCalls.Contains(invocation)) continue;
                    var key = ExtractFirstStringArg(invocation, semanticModel);
                    if (key is null) continue;
                    EmitFact(facts, key, "GetSection", invocation,
                        filePath, semanticModel, stableIdMap);
                    continue;
                }

                // Pattern 4: Configure<T>(config.GetSection("key"))
                if (methodName == "Configure")
                {
                    if (TryGetGetSectionKey(invocation, semanticModel, out var sectionKey))
                        EmitFact(facts, sectionKey!, "Options Configure", invocation,
                            filePath, semanticModel, stableIdMap);
                }
            }
        }

        return facts;
    }

    // ── Receiver type detection ───────────────────────────────────────────────

    private static bool IsConfigurationAccess(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(expression);
        var typeName = typeInfo.Type?.ToDisplayString() ?? "";
        return typeName.Contains("IConfiguration");
    }

    // ── Configure<T>(GetSection) helpers ─────────────────────────────────────

    /// <summary>
    /// Finds all GetSection invocations that appear as direct arguments to Configure calls.
    /// These are handled under pattern 4 and skipped in pattern 3.
    /// </summary>
    private static HashSet<InvocationExpressionSyntax> CollectGetSectionsInConfigureCalls(SyntaxNode root)
    {
        var result = new HashSet<InvocationExpressionSyntax>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Name.Identifier.Text != "Configure") continue;

            foreach (var arg in invocation.ArgumentList.Arguments)
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

    /// <summary>
    /// Tries to extract the section key from a Configure&lt;T&gt;(config.GetSection("key")) call.
    /// </summary>
    private static bool TryGetGetSectionKey(
        InvocationExpressionSyntax configureInvocation,
        SemanticModel semanticModel,
        out string? key)
    {
        key = null;
        foreach (var arg in configureInvocation.ArgumentList.Arguments)
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

    private static string? ExtractBracketStringArg(
        BracketedArgumentListSyntax argList,
        SemanticModel semanticModel)
    {
        if (argList.Arguments.Count == 0) return null;
        var firstArg = argList.Arguments[0].Expression;
        var constantValue = semanticModel.GetConstantValue(firstArg);
        if (constantValue.HasValue && constantValue.Value is string s) return s;
        if (firstArg is LiteralExpressionSyntax lit &&
            lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;
        return null; // dynamic key — cannot extract
    }

    private static string? ExtractFirstStringArg(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        if (invocation.ArgumentList.Arguments.Count == 0) return null;
        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        var constantValue = semanticModel.GetConstantValue(firstArg);
        if (constantValue.HasValue && constantValue.Value is string s) return s;
        if (firstArg is LiteralExpressionSyntax lit &&
            lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;
        return null; // dynamic key — cannot extract
    }

    // ── Fact emission ─────────────────────────────────────────────────────────

    private static void EmitFact(
        List<ExtractedFact> facts,
        string key,
        string usagePattern,
        SyntaxNode node,
        FilePath filePath,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, StableId>? stableIdMap)
    {
        var containingSymbol = FindContainingSymbol(node, semanticModel);
        var symbolIdStr = containingSymbol is not null
            ? GetSymbolId(containingSymbol)
            : null;

        StableId stableId = default;
        if (symbolIdStr is not null)
            stableIdMap?.TryGetValue(symbolIdStr, out stableId);

        var lineSpan = node.GetLocation().GetLineSpan();
        facts.Add(new ExtractedFact(
            SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
            StableId: stableId == default ? null : stableId,
            Kind: FactKind.Config,
            Value: $"{key}|{usagePattern}",
            FilePath: filePath,
            LineStart: lineSpan.StartLinePosition.Line + 1,
            LineEnd: lineSpan.EndLinePosition.Line + 1,
            Confidence: Confidence.High));
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
