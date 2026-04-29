namespace CodeMap.Roslyn.Extraction;

using System.Text.RegularExpressions;
using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks Roslyn SyntaxTrees to extract database table usage facts.
/// Supports three detection patterns:
///   1. EF Core DbSet&lt;T&gt; properties on DbContext-derived classes
///   2. [Table] attribute on entity classes (standalone, not captured via DbSet)
///   3. Raw SQL strings in ExecuteSqlRaw / Dapper Execute / Query calls (Confidence.Medium)
/// </summary>
internal static class DbTableExtractor
{
    // Matches FROM/INTO/UPDATE/JOIN followed by a table identifier (simple or schema-qualified)
    private static readonly Regex[] SqlTablePatterns =
    [
        new Regex(@"(?i)\bFROM\s+(\[?\w+\]?\.?\[?\w+\]?)",   RegexOptions.Compiled),
        new Regex(@"(?i)\bINTO\s+(\[?\w+\]?\.?\[?\w+\]?)",   RegexOptions.Compiled),
        new Regex(@"(?i)\bUPDATE\s+(\[?\w+\]?\.?\[?\w+\]?)", RegexOptions.Compiled),
        new Regex(@"(?i)\bJOIN\s+(\[?\w+\]?\.?\[?\w+\]?)",   RegexOptions.Compiled),
    ];

    // SQL keywords to reject as false-positive table names
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "SET", "TABLE", "DATABASE", "VIEW", "INDEX",
        "INNER", "OUTER", "LEFT", "RIGHT", "CROSS", "FULL", "ON",
    };

    // DB-engine metadata schemas — these are the engine's own catalog tables,
    // not user data. Any match (schema-qualified or bare prefix) is dropped.
    // Covers SQL Server (sys, INFORMATION_SCHEMA), Postgres (pg_catalog,
    // information_schema), MySQL (mysql, performance_schema, information_schema),
    // and SQLite (sqlite_master, sqlite_sequence, sqlite_*).
    private static readonly HashSet<string> MetadataSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys", "information_schema", "pg_catalog", "mysql", "performance_schema",
    };

    private static bool IsMetadataTable(string tableName)
    {
        // Schema-qualified: drop if "<metadata-schema>.something".
        var dot = tableName.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var schema = tableName[..dot];
            if (MetadataSchemas.Contains(schema)) return true;
        }
        // SQLite: catalog tables are unprefixed and start with "sqlite_".
        if (tableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Method names treated as SQL execution points
    private static readonly HashSet<string> SqlMethodNames = new(StringComparer.Ordinal)
    {
        "ExecuteSqlRaw", "ExecuteSqlInterpolated",
        "FromSqlRaw", "FromSqlInterpolated",
        "Execute", "ExecuteAsync",
        "Query", "QueryAsync",
    };

    /// <summary>
    /// Extracts database table facts from all syntax trees in the compilation.
    /// </summary>
    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        if (compilation.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            return VbNet.VbDbTableExtractor.ExtractAll(compilation, solutionDir, stableIdMap);
        var facts = new List<ExtractedFact>();
        // Track entity type FQNs captured via DbSet to avoid standalone [Table] duplicates
        var capturedEntityFqns = new HashSet<string>(StringComparer.Ordinal);
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath is null or "") continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var filePath = MakeRepoRelative(syntaxTree.FilePath, normalizedDir);

            // === Pattern 1: DbSet<T> properties on DbContext-derived classes ===
            foreach (var propertyDecl in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var propertySymbol = semanticModel.GetDeclaredSymbol(propertyDecl);
                if (propertySymbol is null) continue;

                if (propertySymbol.Type is not INamedTypeSymbol propertyType) continue;
                if (!IsDbSetType(propertyType)) continue;

                if (!InheritsFromDbContext(propertySymbol.ContainingType)) continue;

                // Entity type is the first type argument of DbSet<T>
                var entityType = propertyType.TypeArguments.Length > 0
                    ? propertyType.TypeArguments[0] as INamedTypeSymbol
                    : null;

                // Default table name = property name; override if [Table] on entity
                string tableName = propertySymbol.Name;
                string? schema = null;

                if (entityType is not null)
                {
                    var (attrName, attrSchema) = ExtractTableAttribute(entityType);
                    if (attrName is not null)
                    {
                        tableName = attrName;
                        schema = attrSchema;
                    }
                    // Track entity FQN to prevent duplicate in standalone [Table] pass
                    capturedEntityFqns.Add(entityType.ToDisplayString());
                }

                var fullTableName = schema is not null ? $"{schema}.{tableName}" : tableName;
                var entityName = entityType?.Name ?? "unknown";
                var symbolIdStr = GetSymbolId(propertySymbol);
                var stableId = LookupStableId(symbolIdStr, stableIdMap);
                var lineSpan = propertyDecl.GetLocation().GetLineSpan();

                facts.Add(new ExtractedFact(
                    SymbolId: SymbolId.From(symbolIdStr),
                    StableId: stableId,
                    Kind: FactKind.DbTable,
                    Value: $"{fullTableName}|DbSet<{entityName}>",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }

            // === Pattern 2: Standalone [Table] on entity classes not yet captured ===
            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl);
                if (classSymbol is null) continue;

                var (attrName, attrSchema) = ExtractTableAttribute(classSymbol);
                if (attrName is null) continue;

                // Skip if already captured via DbSet<T>
                if (capturedEntityFqns.Contains(classSymbol.ToDisplayString())) continue;

                var fullTableName = attrSchema is not null ? $"{attrSchema}.{attrName}" : attrName;
                var symbolIdStr = GetSymbolId(classSymbol);
                var stableId = LookupStableId(symbolIdStr, stableIdMap);
                var lineSpan = classDecl.GetLocation().GetLineSpan();

                facts.Add(new ExtractedFact(
                    SymbolId: SymbolId.From(symbolIdStr),
                    StableId: stableId,
                    Kind: FactKind.DbTable,
                    Value: $"{fullTableName}|[Table]",
                    FilePath: filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence: Confidence.High));
            }

            // === Pattern 3: Raw SQL strings in known execution method calls ===
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsSqlExecutionMethod(invocation)) continue;

                var sqlString = ExtractFirstStringArg(invocation, semanticModel);
                if (sqlString is null) continue;

                var tableNames = ExtractTableNamesFromSql(sqlString);
                if (tableNames.Count == 0) continue;

                var containingSymbol = FindContainingSymbol(invocation, semanticModel);
                var symbolIdStr = containingSymbol is not null
                    ? GetSymbolId(containingSymbol)
                    : null;
                var stableId = LookupStableId(symbolIdStr, stableIdMap);
                var lineSpan = invocation.GetLocation().GetLineSpan();

                foreach (var tableName in tableNames)
                {
                    facts.Add(new ExtractedFact(
                        SymbolId: symbolIdStr is not null
                            ? SymbolId.From(symbolIdStr)
                            : SymbolId.Empty,
                        StableId: stableId,
                        Kind: FactKind.DbTable,
                        Value: $"{tableName}|Raw SQL",
                        FilePath: filePath,
                        LineStart: lineSpan.StartLinePosition.Line + 1,
                        LineEnd: lineSpan.EndLinePosition.Line + 1,
                        Confidence: Confidence.Medium));
                }
            }
        }

        return facts;
    }

    // ── Type detection helpers ────────────────────────────────────────────────

    private static bool IsDbSetType(INamedTypeSymbol typeSymbol)
    {
        // Name-based check works for both real EF Core and stubs
        if (typeSymbol.Name != "DbSet") return false;
        if (typeSymbol.TypeArguments.Length != 1) return false;
        // Check namespace contains EntityFrameworkCore (or accept stubs that don't have that namespace)
        var ns = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.Contains("EntityFrameworkCore") || ns == "Microsoft.EntityFrameworkCore";
    }

    private static bool InheritsFromDbContext(INamedTypeSymbol? typeSymbol)
    {
        var current = typeSymbol?.BaseType;
        while (current is not null)
        {
            if (current.Name == "DbContext") return true;
            current = current.BaseType;
        }
        return false;
    }

    private static (string? Name, string? Schema) ExtractTableAttribute(INamedTypeSymbol typeSymbol)
    {
        foreach (var attr in typeSymbol.GetAttributes())
        {
            var attrClassName = attr.AttributeClass?.Name;
            if (attrClassName is not "TableAttribute" and not "Table") continue;

            string? name = null;
            if (attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string s)
                name = s;

            if (name is null) continue;

            string? schema = null;
            foreach (var namedArg in attr.NamedArguments)
            {
                if (namedArg.Key == "Schema" && namedArg.Value.Value is string sc)
                {
                    schema = sc;
                    break;
                }
            }

            return (name, schema);
        }
        return (null, null);
    }

    // ── SQL execution detection ───────────────────────────────────────────────

    private static bool IsSqlExecutionMethod(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            return SqlMethodNames.Contains(memberAccess.Name.Identifier.Text);
        return false;
    }

    private static string? ExtractFirstStringArg(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        if (invocation.ArgumentList.Arguments.Count == 0) return null;
        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        var cv = semanticModel.GetConstantValue(firstArg);
        if (cv.HasValue && cv.Value is string s) return s;
        if (firstArg is LiteralExpressionSyntax lit &&
            lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;
        return null;
    }

    private static IReadOnlyList<string> ExtractTableNamesFromSql(string sql)
    {
        // Normalize whitespace so multi-line SQL strings match single-line patterns.
        var normalizedSql = Regex.Replace(sql, @"\s+", " ").Trim();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in SqlTablePatterns)
        {
            foreach (Match match in pattern.Matches(normalizedSql))
            {
                var raw = match.Groups[1].Value
                    .Replace("[", "", StringComparison.Ordinal)
                    .Replace("]", "", StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(raw)
                    && !SqlKeywords.Contains(raw)
                    && !IsMetadataTable(raw))
                    tables.Add(raw);
            }
        }
        return tables.ToList();
    }

    // ── Symbol helpers ────────────────────────────────────────────────────────

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

    private static StableId? LookupStableId(
        string? symbolIdStr,
        IReadOnlyDictionary<string, StableId>? stableIdMap)
    {
        if (symbolIdStr is null || stableIdMap is null) return null;
        stableIdMap.TryGetValue(symbolIdStr, out var stableId);
        return stableId == default ? null : stableId;
    }

    private static FilePath MakeRepoRelative(string filePath, string normalizedDir)
    {
        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            return FilePath.From(normalized[normalizedDir.Length..]);
        return FilePath.From(Path.GetFileName(normalized));
    }
}
