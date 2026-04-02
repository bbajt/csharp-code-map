namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>
/// VB.NET counterpart of <see cref="DbTableExtractor"/>.
/// Detects DbSet(Of T) properties and [Table] attributes. Raw SQL omitted (pattern 3).
/// Uses name-based type detection (works with stubs — no real EF Core NuGet required).
/// </summary>
internal static class VbDbTableExtractor
{
    /// <summary>
    /// Extracts database table facts from all VB.NET syntax trees in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var facts = new List<ExtractedFact>();
        var capturedEntityFqns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath is null or "") continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            // === Pattern 1: DbSet(Of T) properties on DbContext-derived classes ===
            foreach (var classBlock in syntaxTree.GetRoot()
                .DescendantNodes().OfType<ClassBlockSyntax>())
            {
                if (!InheritsDbContext(classBlock)) continue;

                foreach (var member in classBlock.Members)
                {
                    // VB auto-properties are PropertyStatementSyntax directly;
                    // full property blocks wrap PropertyStatementSyntax inside PropertyBlockSyntax
                    PropertyStatementSyntax? propStmt = member switch
                    {
                        PropertyStatementSyntax ps => ps,
                        PropertyBlockSyntax pb => pb.PropertyStatement,
                        _ => null
                    };
                    if (propStmt is null) continue;

                    var asClause = propStmt.AsClause as SimpleAsClauseSyntax;
                    if (asClause?.Type is not GenericNameSyntax genericType) continue;
                    if (genericType.Identifier.Text != "DbSet") continue;
                    if (genericType.TypeArgumentList.Arguments.Count != 1) continue;

                    var typeArgSyntax = genericType.TypeArgumentList.Arguments[0];
                    var typeArgSymbol = semanticModel.GetTypeInfo(typeArgSyntax).Type;
                    var entityFqn = typeArgSymbol?.ToDisplayString() ?? typeArgSyntax.ToString();
                    var entityDisplayName = typeArgSymbol?.Name ?? typeArgSyntax.ToString();
                    var tableName = propStmt.Identifier.Text;
                    capturedEntityFqns.Add(entityFqn);

                    var propSymbol = semanticModel.GetDeclaredSymbol(propStmt);
                    var symbolIdStr = propSymbol is not null ? GetSymbolId(propSymbol) : null;
                    StableId stableId = default;
                    if (symbolIdStr is not null) stableIdMap?.TryGetValue(symbolIdStr, out stableId);

                    var lineSpan = (member as PropertyBlockSyntax)?.GetLocation().GetLineSpan()
                        ?? propStmt.GetLocation().GetLineSpan();

                    facts.Add(new ExtractedFact(
                        SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
                        StableId: stableId == default ? null : stableId,
                        Kind: FactKind.DbTable,
                        Value: $"{tableName}|DbSet<{entityDisplayName}>",
                        FilePath: filePath,
                        LineStart: lineSpan.StartLinePosition.Line + 1,
                        LineEnd: lineSpan.EndLinePosition.Line + 1,
                        Confidence: Confidence.High));
                }
            }

            // === Pattern 2: standalone [Table("...")] attribute ===
            foreach (var classBlock in syntaxTree.GetRoot()
                .DescendantNodes().OfType<ClassBlockSyntax>())
            {
                var tableAttr = classBlock.ClassStatement.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .FirstOrDefault(a => a.Name.ToString() is "Table" or "TableAttribute");
                if (tableAttr is null) continue;

                var tableName = GetFirstStringArgFromAttr(tableAttr);
                if (tableName is null) continue;

                var classSymbol2 = semanticModel.GetDeclaredSymbol(classBlock) as INamedTypeSymbol;
                var classFqn = classSymbol2?.ToDisplayString()
                    ?? classBlock.ClassStatement.Identifier.Text;
                if (capturedEntityFqns.Contains(classFqn)) continue;

                var classSymbol = semanticModel.GetDeclaredSymbol(classBlock) as INamedTypeSymbol;
                var symbolIdStr = classSymbol is not null ? GetSymbolId(classSymbol) : null;
                StableId stableId = default;
                if (symbolIdStr is not null) stableIdMap?.TryGetValue(symbolIdStr, out stableId);

                var lineSpan = classBlock.GetLocation().GetLineSpan();

                facts.Add(new ExtractedFact(
                    SymbolId: symbolIdStr is not null ? SymbolId.From(symbolIdStr) : SymbolId.Empty,
                    StableId: stableId == default ? null : stableId,
                    Kind: FactKind.DbTable,
                    Value: $"{tableName}|[Table]",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }
        }

        return facts;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool InheritsDbContext(ClassBlockSyntax classBlock)
        => classBlock.Inherits
            .SelectMany(i => i.Types)
            .Any(t => t.ToString().Contains("DbContext"));

    private static string? GetFirstStringArgFromAttr(AttributeSyntax attr)
    {
        if (attr.ArgumentList is null) return null;
        var firstArg = attr.ArgumentList.Arguments.OfType<SimpleArgumentSyntax>().FirstOrDefault();
        if (firstArg?.Expression is LiteralExpressionSyntax lit && lit.Token.Value is string s)
            return s;
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
