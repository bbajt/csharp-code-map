namespace CodeMap.Roslyn;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeMapSymbolKind = CodeMap.Core.Enums.SymbolKind;

/// <summary>
/// Extracts symbols from syntax alone when compilation fails.
/// All extracted symbols have Confidence.Low and no type resolution.
/// </summary>
internal static class SyntacticFallback
{
    public static IReadOnlyList<SymbolCard> Extract(IEnumerable<(string FilePath, string Content)> files)
    {
        var cards = new List<SymbolCard>();

        foreach (var (filePath, content) in files)
        {
            var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
            var root = tree.GetRoot();
            ExtractFromRoot(root, filePath, cards);
        }

        return cards;
    }

    private static void ExtractFromRoot(SyntaxNode root, string filePath, List<SymbolCard> cards)
    {
        foreach (var node in root.DescendantNodes())
        {
            SymbolCard? card = node switch
            {
                TypeDeclarationSyntax type => ExtractType(type, filePath),
                MethodDeclarationSyntax method => ExtractMethod(method, filePath),
                PropertyDeclarationSyntax prop => ExtractProperty(prop, filePath),
                _ => null,
            };

            if (card is not null) cards.Add(card);
        }
    }

    private static SymbolCard? ExtractType(TypeDeclarationSyntax node, string filePath)
    {
        string name = node.Identifier.Text;
        CodeMapSymbolKind kind = node switch
        {
            ClassDeclarationSyntax => CodeMapSymbolKind.Class,
            StructDeclarationSyntax => CodeMapSymbolKind.Struct,
            InterfaceDeclarationSyntax => CodeMapSymbolKind.Interface,
            RecordDeclarationSyntax => CodeMapSymbolKind.Record,
            _ => CodeMapSymbolKind.Class,
        };

        return MakeCard($"{filePath}::{name}", name, kind, filePath, node.GetLocation());
    }

    private static SymbolCard? ExtractMethod(MethodDeclarationSyntax node, string filePath)
    {
        string methodName = node.Identifier.Text;
        string containingType = GetContainingTypeName(node);
        return MakeCard($"{filePath}::{containingType}.{methodName}", methodName,
            CodeMapSymbolKind.Method, filePath, node.GetLocation());
    }

    private static SymbolCard? ExtractProperty(PropertyDeclarationSyntax node, string filePath)
    {
        string name = node.Identifier.Text;
        string containingType = GetContainingTypeName(node);
        return MakeCard($"{filePath}::{containingType}.{name}", name,
            CodeMapSymbolKind.Property, filePath, node.GetLocation());
    }

    private static SymbolCard? MakeCard(string symbolIdStr, string name, CodeMapSymbolKind kind,
        string filePath, Location location)
    {
        FilePath fp;
        try { fp = FilePath.From(Path.GetFileName(filePath.Replace('\\', '/'))); }
        catch { return null; }

        var lineSpan = location.GetLineSpan();
        int spanStart = lineSpan.StartLinePosition.Line + 1;
        int spanEnd = lineSpan.EndLinePosition.Line + 1;

        return new SymbolCard(
            SymbolId: SymbolId.From(Sha8(symbolIdStr) + "-" + name),
            FullyQualifiedName: name,
            Kind: kind,
            Signature: name,
            Documentation: null,
            Namespace: string.Empty,
            ContainingType: null,
            FilePath: fp,
            SpanStart: spanStart,
            SpanEnd: spanEnd,
            Visibility: "unknown",
            CallsTop: [],
            Facts: [],
            SideEffects: [],
            ThrownExceptions: [],
            Evidence: [],
            Confidence: Confidence.Low
        );
    }

    private static string GetContainingTypeName(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent is not null)
        {
            if (parent is TypeDeclarationSyntax type)
                return type.Identifier.Text;
            parent = parent.Parent;
        }
        return "Unknown";
    }

    private static string Sha8(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..8];
    }
}
