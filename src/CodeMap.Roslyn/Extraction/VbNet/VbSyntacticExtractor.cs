namespace CodeMap.Roslyn.Extraction.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using CmRefKind = CodeMap.Core.Enums.RefKind;
using CmResolutionState = CodeMap.Core.Enums.ResolutionState;
using CmSymbolKind = CodeMap.Core.Enums.SymbolKind;

/// <summary>
/// Syntax-only symbol and unresolved-reference extractor for VB.NET projects
/// that fail to compile semantically. Produces <see cref="Confidence.Low"/>
/// symbols with no type resolution or FQN, and unresolved call edges.
/// Mirrors <see cref="SyntacticFallback"/> and <see cref="SyntacticReferenceExtractor"/>
/// for VB.NET (VisualBasicSyntaxWalker).
/// </summary>
internal static class VbSyntacticExtractor
{
    /// <summary>
    /// Extracts symbols and unresolved references from the given VB.NET source files.
    /// </summary>
    public static (IReadOnlyList<SymbolCard> Symbols, IReadOnlyList<ExtractedReference> Refs)
        ExtractAll(IEnumerable<(string FilePath, string Content)> files, string solutionDir)
    {
        var symbols = new List<SymbolCard>();
        var refs = new List<ExtractedReference>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var (absolutePath, content) in files)
        {
            if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(content)) continue;

            try
            {
                var normalizedPath = absolutePath.Replace('\\', '/');
                string relPath = normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)
                    ? normalizedPath[normalizedDir.Length..]
                    : Path.GetFileName(normalizedPath);

                var tree = VisualBasicSyntaxTree.ParseText(content, path: absolutePath);
                var root = tree.GetRoot();
                var walker = new VbSyntaxWalker(relPath, symbols, refs);
                walker.Visit(root);
            }
            catch
            {
                // Skip files that cannot be parsed or walked — one bad file must not
                // abort the entire syntactic extraction for the project.
            }
        }

        return (symbols, refs);
    }

    /// <summary>
    /// Extracts symbols and unresolved references from all VB.NET documents in a project.
    /// </summary>
    public static (IReadOnlyList<SymbolCard> Symbols, IReadOnlyList<ExtractedReference> Refs)
        ExtractAll(Project project, string solutionDir)
    {
        var files = project.Documents
            .Where(d => d.FilePath?.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) == true)
            .Select(d =>
            {
                try { return (d.FilePath!, File.ReadAllText(d.FilePath!)); }
                catch { return (string.Empty, string.Empty); }
            })
            .Where(f => !string.IsNullOrEmpty(f.Item1));

        return ExtractAll(files, solutionDir);
    }

    private sealed class VbSyntaxWalker : VisualBasicSyntaxWalker
    {
        private readonly string _relPath;
        private readonly List<SymbolCard> _symbols;
        private readonly List<ExtractedReference> _refs;
        // Stack-based container tracking: push on enter, pop on exit for each type block.
        // Avoids _currentContainer mutation bugs with nested types and interface/struct bodies.
        private readonly Stack<string> _containers = new();
        private string CurrentContainer => _containers.Count > 0 ? _containers.Peek() : "";

        public VbSyntaxWalker(string relPath, List<SymbolCard> symbols, List<ExtractedReference> refs)
        {
            _relPath = relPath;
            _symbols = symbols;
            _refs = refs;
        }

        public override void VisitClassBlock(ClassBlockSyntax node)
        {
            var name = node.ClassStatement.Identifier.Text;
            // Add symbol using the *current* container (outer type or "" for top-level),
            // then push own name so that nested members see this as their container.
            AddSymbol(name, CmSymbolKind.Class, node);
            _containers.Push(name);
            base.VisitClassBlock(node);
            _containers.Pop();
        }

        public override void VisitInterfaceBlock(InterfaceBlockSyntax node)
        {
            var name = node.InterfaceStatement.Identifier.Text;
            AddSymbol(name, CmSymbolKind.Interface, node);
            _containers.Push(name);
            base.VisitInterfaceBlock(node);
            _containers.Pop();
        }

        public override void VisitModuleBlock(ModuleBlockSyntax node)
        {
            var name = node.ModuleStatement.Identifier.Text;
            AddSymbol(name, CmSymbolKind.Class, node); // VB Module → Class (sealed static)
            _containers.Push(name);
            base.VisitModuleBlock(node);
            _containers.Pop();
        }

        public override void VisitStructureBlock(StructureBlockSyntax node)
        {
            var name = node.StructureStatement.Identifier.Text;
            AddSymbol(name, CmSymbolKind.Struct, node);
            _containers.Push(name);
            base.VisitStructureBlock(node);
            _containers.Pop();
        }

        public override void VisitEnumBlock(EnumBlockSyntax node)
        {
            var name = node.EnumStatement.Identifier.Text;
            AddSymbol(name, CmSymbolKind.Enum, node);
            base.VisitEnumBlock(node);
        }

        public override void VisitMethodBlock(MethodBlockSyntax node)
        {
            var name = node.SubOrFunctionStatement.Identifier.Text;
            AddSymbol(name, CmSymbolKind.Method, node);

            // Emit unresolved Call refs for each invocation in the method body
            foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? toName = null;
                string? containerHint = null;

                if (invocation.Expression is MemberAccessExpressionSyntax ma)
                {
                    toName = ma.Name.Identifier.Text;
                    containerHint = TruncateHint(ma.Expression.ToString(), 100);
                }
                else if (invocation.Expression is IdentifierNameSyntax id)
                {
                    toName = id.Identifier.Text;
                }

                if (toName is null) continue;

                FilePath filePath;
                try { filePath = FilePath.From(_relPath); }
                catch { continue; }

                var container = CurrentContainer;
                var fromId = string.IsNullOrEmpty(container)
                    ? SymbolId.From($"__unknown__::{name}")
                    : SymbolId.From($"{container}::{name}");

                var span = invocation.GetLocation().GetLineSpan();
                _refs.Add(new ExtractedReference(
                    FromSymbol: fromId,
                    ToSymbol: SymbolId.Empty,
                    Kind: CmRefKind.Call,
                    FilePath: filePath,
                    LineStart: span.StartLinePosition.Line + 1,
                    LineEnd: span.EndLinePosition.Line + 1,
                    ResolutionState: CmResolutionState.Unresolved,
                    ToName: toName,
                    ToContainerHint: containerHint));
            }

            base.VisitMethodBlock(node);
        }

        private void AddSymbol(string name, CmSymbolKind kind, SyntaxNode node)
        {
            FilePath fp;
            try { fp = FilePath.From(_relPath); }
            catch { return; }

            // Use Container::Name format — consistent with the fromId format emitted by
            // VisitMethodBlock, enabling ref→symbol linkage in syntactic resolution.
            // Top-level symbols (no container yet) use just their name.
            var container = CurrentContainer;
            SymbolId symbolId;
            try
            {
                symbolId = string.IsNullOrEmpty(container)
                    ? SymbolId.From(name)
                    : SymbolId.From($"{container}::{name}");
            }
            catch { return; }

            var span = node.GetLocation().GetLineSpan();

            _symbols.Add(new SymbolCard(
                SymbolId: symbolId,
                FullyQualifiedName: name,
                Kind: kind,
                Signature: name,
                Documentation: null,
                Namespace: string.Empty,
                ContainingType: null,
                FilePath: fp,
                SpanStart: span.StartLinePosition.Line + 1,
                SpanEnd: span.EndLinePosition.Line + 1,
                Visibility: "unknown",
                CallsTop: [],
                Facts: [],
                SideEffects: [],
                ThrownExceptions: [],
                Evidence: [],
                Confidence: Confidence.Low));
        }

        private static string TruncateHint(string text, int maxLength)
            => text.Length <= maxLength ? text : text[..maxLength];
    }
}
