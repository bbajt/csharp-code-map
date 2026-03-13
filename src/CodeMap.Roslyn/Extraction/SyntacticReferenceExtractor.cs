namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using CmRefKind = CodeMap.Core.Enums.RefKind;
using CmResolutionState = CodeMap.Core.Enums.ResolutionState;

/// <summary>
/// Extracts unresolved reference edges from AST nodes without a SemanticModel.
/// Runs only in the compilation fallback path. All produced edges have
/// ResolutionState.Unresolved and use simplified syntactic symbol IDs
/// (format: "ClassName::MethodName" or "ClassName").
/// </summary>
internal static class SyntacticReferenceExtractor
{
    private const int MaxContainerHintLength = 100;

    /// <summary>
    /// Extracts unresolved reference edges from a list of source files.
    /// </summary>
    public static IReadOnlyList<ExtractedReference> ExtractAll(
        IEnumerable<(string FilePath, string Content)> files,
        string solutionDir)
    {
        var results = new List<ExtractedReference>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var (absolutePath, content) in files)
        {
            if (string.IsNullOrEmpty(absolutePath)) continue;

            var normalizedPath = absolutePath.Replace('\\', '/');
            string relPath = normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)
                ? normalizedPath[normalizedDir.Length..]
                : System.IO.Path.GetFileName(normalizedPath);

            var filePath = FilePath.From(relPath);
            var tree = CSharpSyntaxTree.ParseText(content, path: absolutePath);
            var root = tree.GetRoot();

            ExtractFromRoot(root, tree, filePath, results);
        }

        return results;
    }

    private static void ExtractFromRoot(
        SyntaxNode root,
        SyntaxTree tree,
        FilePath filePath,
        List<ExtractedReference> results)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    {
                        var (toName, containerHint) = ExtractCallTarget(invocation);
                        if (toName is null) break;

                        var fromSymbol = FindContainingSymbolSyntactic(node);
                        var span = tree.GetLineSpan(node.Span);
                        results.Add(new ExtractedReference(
                            FromSymbol: fromSymbol,
                            ToSymbol: SymbolId.Empty,
                            Kind: CmRefKind.Call,
                            FilePath: filePath,
                            LineStart: span.StartLinePosition.Line + 1,
                            LineEnd: span.EndLinePosition.Line + 1,
                            ResolutionState: CmResolutionState.Unresolved,
                            ToName: toName,
                            ToContainerHint: containerHint));
                        break;
                    }

                case ObjectCreationExpressionSyntax creation:
                    {
                        var typeName = ExtractCreatedTypeName(creation);
                        if (typeName is null) break;

                        var fromSymbol = FindContainingSymbolSyntactic(node);
                        var span = tree.GetLineSpan(node.Span);
                        results.Add(new ExtractedReference(
                            FromSymbol: fromSymbol,
                            ToSymbol: SymbolId.Empty,
                            Kind: CmRefKind.Instantiate,
                            FilePath: filePath,
                            LineStart: span.StartLinePosition.Line + 1,
                            LineEnd: span.EndLinePosition.Line + 1,
                            ResolutionState: CmResolutionState.Unresolved,
                            ToName: typeName,
                            ToContainerHint: null));
                        break;
                    }

                case MemberAccessExpressionSyntax memberAccess
                    when node.Parent is not MemberAccessExpressionSyntax: // avoid double-emit for chained access
                    {
                        // Skip if this is the expression of an invocation (handled by invocation case)
                        if (node.Parent is InvocationExpressionSyntax inv && inv.Expression == node)
                            break;

                        var memberName = memberAccess.Name.Identifier.Text;
                        var receiverText = TruncateHint(memberAccess.Expression.ToString(), MaxContainerHintLength);

                        var kind = IsAssignmentTarget(memberAccess) ? CmRefKind.Write : CmRefKind.Read;
                        var fromSymbol = FindContainingSymbolSyntactic(node);
                        var span = tree.GetLineSpan(node.Span);
                        results.Add(new ExtractedReference(
                            FromSymbol: fromSymbol,
                            ToSymbol: SymbolId.Empty,
                            Kind: kind,
                            FilePath: filePath,
                            LineStart: span.StartLinePosition.Line + 1,
                            LineEnd: span.EndLinePosition.Line + 1,
                            ResolutionState: CmResolutionState.Unresolved,
                            ToName: memberName,
                            ToContainerHint: receiverText));
                        break;
                    }
            }
        }
    }

    private static (string? ToName, string? ContainerHint) ExtractCallTarget(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess =>
                (memberAccess.Name.Identifier.Text,
                 TruncateHint(memberAccess.Expression.ToString(), MaxContainerHintLength)),

            IdentifierNameSyntax identifier =>
                (identifier.Identifier.Text, null),

            _ => (null, null)
        };
    }

    private static string? ExtractCreatedTypeName(ObjectCreationExpressionSyntax creation)
    {
        return creation.Type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null
        };
    }

    private static SymbolId FindContainingSymbolSyntactic(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is MethodDeclarationSyntax method)
            {
                var typeName = FindContainingTypeName(method);
                return typeName is not null
                    ? SymbolId.From($"{typeName}::{method.Identifier.Text}")
                    : SymbolId.From($"__unknown__::{method.Identifier.Text}");
            }

            if (current is TypeDeclarationSyntax type)
                return SymbolId.From(type.Identifier.Text);

            current = current.Parent;
        }

        return SymbolId.From("__unknown__");
    }

    private static string? FindContainingTypeName(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is TypeDeclarationSyntax type)
                return type.Identifier.Text;
            current = current.Parent;
        }

        return null;
    }

    private static bool IsAssignmentTarget(MemberAccessExpressionSyntax memberAccess)
    {
        var parent = memberAccess.Parent;
        return parent is AssignmentExpressionSyntax assignment && assignment.Left == memberAccess;
    }

    private static string TruncateHint(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength];
}
