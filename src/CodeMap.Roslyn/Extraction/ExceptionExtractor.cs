namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract exception throw facts (<see cref="FactKind.Exception"/>).
/// Detects three patterns:
///   <list type="bullet">
///     <item><c>throw new ExceptionType(...)</c> — context "throw new" or "throw new (nameof guard)"</item>
///     <item><c>throw;</c> in a catch block — context "re-throw", type from catch declaration</item>
///     <item><c>x ?? throw new ExceptionType()</c> — context "throw expression"</item>
///   </list>
/// Uses SemanticModel for type resolution with syntax-level fallback.
/// </summary>
internal static class ExceptionExtractor
{
    /// <summary>
    /// Extracts exception throw facts from all syntax trees in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        if (compilation.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            return VbNet.VbExceptionExtractor.ExtractAll(compilation, solutionDir, stableIdMap);
        var facts = new List<ExtractedFact>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath is null or "") continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            // Pattern 1 + 3: throw new ExceptionType(...) and throw expressions
            // Collect the thrown ObjectCreationExpression from both throw statements and throw expressions
            var throwCreations = root.DescendantNodes()
                .OfType<ThrowExpressionSyntax>()
                .Select(t => t.Expression)
                .Concat(
                    root.DescendantNodes()
                        .OfType<ThrowStatementSyntax>()
                        .Where(s => s.Expression is not null)
                        .Select(s => s.Expression!));

            foreach (var throwExpr in throwCreations)
            {
                if (throwExpr is not ObjectCreationExpressionSyntax creation) continue;

                var typeInfo = semanticModel.GetTypeInfo(creation);
                var typeName = typeInfo.Type?.Name ?? creation.Type.ToString();

                string context;
                if (HasNameofArg(creation))
                    context = "throw new (nameof guard)";
                else if (creation.Parent is ThrowExpressionSyntax)
                    context = "throw expression";
                else
                    context = "throw new";

                EmitFact(facts, typeName, context, creation, semanticModel, filePath, stableIdMap);
            }

            // Pattern 2: bare throw; in catch blocks
            foreach (var throwStmt in root.DescendantNodes()
                         .OfType<ThrowStatementSyntax>()
                         .Where(s => s.Expression is null))
            {
                var catchClause = throwStmt.Ancestors()
                    .OfType<CatchClauseSyntax>()
                    .FirstOrDefault();

                var typeName = catchClause?.Declaration is not null
                    ? catchClause.Declaration.Type.ToString()
                    : "Exception";

                EmitFact(facts, typeName, "re-throw", throwStmt, semanticModel, filePath, stableIdMap);
            }
        }

        return facts;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        if (symbolIdStr is not null)
            stableIdMap?.TryGetValue(symbolIdStr, out stableId);

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

    private static bool HasNameofArg(ObjectCreationExpressionSyntax creation)
    {
        if (creation.ArgumentList is null) return false;
        return creation.ArgumentList.Arguments.Any(arg =>
            arg.Expression is InvocationExpressionSyntax inv &&
            inv.Expression is IdentifierNameSyntax id &&
            id.Identifier.Text == "nameof");
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
