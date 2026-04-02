namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="ExceptionExtractor"/>.
/// Detects Throw New ExceptionType(...) statements and bare re-throws in Catch blocks.
/// VB.NET has no throw expressions — only ThrowStatementSyntax.
/// </summary>
internal static class VbExceptionExtractor
{
    /// <summary>
    /// Extracts exception throw facts from all VB.NET syntax trees in the compilation.
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

            foreach (var throwStmt in syntaxTree.GetRoot()
                .DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression is ObjectCreationExpressionSyntax creation)
                {
                    // Throw New ExceptionType(...)
                    var typeInfo = semanticModel.GetTypeInfo(creation);
                    var typeName = typeInfo.Type?.Name ?? creation.Type.ToString();

                    string context;
                    if (HasNameofArg(creation))
                        context = "throw new (nameof guard)";
                    else
                        context = "throw new";

                    EmitFact(facts, typeName, context, throwStmt,
                        semanticModel, filePath, stableIdMap);
                }
                else if (throwStmt.Expression is null)
                {
                    // Bare Throw — re-throw inside a Catch block.
                    // Use semantic model to get the simple type name (not syntax text which
                    // may be fully-qualified, e.g. "System.IO.IOException" instead of "IOException").
                    var catchBlock = throwStmt.Ancestors().OfType<CatchBlockSyntax>().FirstOrDefault();
                    var asClauseType = catchBlock?.CatchStatement?.AsClause?.Type;
                    var typeName = asClauseType is not null
                        ? (semanticModel.GetTypeInfo(asClauseType).Type?.Name ?? asClauseType.ToString())
                        : "Exception";
                    EmitFact(facts, typeName, "re-throw", throwStmt,
                        semanticModel, filePath, stableIdMap);
                }
            }
        }

        return facts;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasNameofArg(ObjectCreationExpressionSyntax creation)
    {
        if (creation.ArgumentList is null) return false;
        return creation.ArgumentList.Arguments.OfType<SimpleArgumentSyntax>()
            .Any(a => a.Expression is InvocationExpressionSyntax inv &&
                      inv.Expression is IdentifierNameSyntax id &&
                      id.Identifier.Text.Equals("NameOf", StringComparison.OrdinalIgnoreCase));
    }

    private static void EmitFact(
        List<ExtractedFact> facts,
        string typeName,
        string context,
        SyntaxNode node,
        SemanticModel semanticModel,
        FilePath filePath,
        IReadOnlyDictionary<string, StableId>? stableIdMap)
    {
        var containingSymbol = FindContainingSymbol(node, semanticModel);
        var symbolIdStr = containingSymbol is not null ? GetSymbolId(containingSymbol) : null;

        StableId stableId = default;
        if (symbolIdStr is not null) stableIdMap?.TryGetValue(symbolIdStr, out stableId);

        var lineSpan = node.GetLocation().GetLineSpan();

        facts.Add(new ExtractedFact(
            SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
            StableId: stableId == default ? null : stableId,
            Kind: FactKind.Exception,
            Value: $"{typeName}|{context}",
            FilePath: filePath,
            LineStart: lineSpan.StartLinePosition.Line + 1,
            LineEnd: lineSpan.EndLinePosition.Line + 1,
            Confidence: Confidence.High));
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
