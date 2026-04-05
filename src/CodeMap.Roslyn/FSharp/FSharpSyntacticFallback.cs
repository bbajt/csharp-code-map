namespace CodeMap.Roslyn.FSharp;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Syntactic-only extraction for F# projects that fail FCS type-check.
/// Produces degraded symbols from source text patterns — no semantic info.
/// Mirrors SyntacticFallback (C#) and VbSyntacticExtractor (VB.NET).
/// </summary>
internal static partial class FSharpSyntacticFallback
{
    public static (IReadOnlyList<SymbolCard> Symbols, IReadOnlyList<ExtractedReference> Refs)
        ExtractAll(string fsprojPath, string solutionDir)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(fsprojPath))!;
        var sourceFiles = FSharpProjectAnalyzer.GetSourceFiles(fsprojPath, projectDir);
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';
        var projectName = Path.GetFileNameWithoutExtension(fsprojPath);

        var symbols = new List<SymbolCard>();

        foreach (var fsFile in sourceFiles)
        {
            try
            {
                var content = File.ReadAllText(fsFile);
                var filePath = FSharpSymbolMapper.MakeRepoRelative(fsFile, normalizedDir);
                ExtractFromSource(content, filePath, projectName, symbols);
            }
            catch { /* skip unreadable files */ }
        }

        // No refs in syntactic mode — no semantic info available
        return (symbols, Array.Empty<ExtractedReference>());
    }

    private static void ExtractFromSource(
        string content, string filePath, string projectName, List<SymbolCard> symbols)
    {
        var lines = content.Split('\n');
        string currentNamespace = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r').TrimStart();

            // namespace SomeNamespace
            if (line.StartsWith("namespace "))
            {
                currentNamespace = line[10..].Trim();
                continue;
            }

            // module SomeName
            var moduleMatch = ModuleRegex().Match(line);
            if (moduleMatch.Success)
            {
                var name = moduleMatch.Groups[1].Value;
                var fqn = string.IsNullOrEmpty(currentNamespace) ? name : $"{currentNamespace}.{name}";
                symbols.Add(BuildSyntacticCard(
                    $"T:{fqn}", fqn, name, SymbolKind.Class,
                    filePath, i + 1, projectName, currentNamespace));
                continue;
            }

            // type SomeName (class/record/union/interface/struct)
            var typeMatch = TypeRegex().Match(line);
            if (typeMatch.Success)
            {
                var name = typeMatch.Groups[1].Value;
                var fqn = string.IsNullOrEmpty(currentNamespace) ? name : $"{currentNamespace}.{name}";
                var kind = line.Contains("interface") ? SymbolKind.Interface
                    : line.Contains("struct") ? SymbolKind.Struct
                    : SymbolKind.Class;
                symbols.Add(BuildSyntacticCard(
                    $"T:{fqn}", fqn, name, kind,
                    filePath, i + 1, projectName, currentNamespace));
                continue;
            }

            // let someName (top-level function in module)
            var letMatch = LetRegex().Match(line);
            if (letMatch.Success)
            {
                var name = letMatch.Groups[1].Value;
                var fqn = string.IsNullOrEmpty(currentNamespace) ? name : $"{currentNamespace}.{name}";
                symbols.Add(BuildSyntacticCard(
                    $"M:{fqn}", fqn, name, SymbolKind.Method,
                    filePath, i + 1, projectName, currentNamespace));
            }
        }
    }

    private static SymbolCard BuildSyntacticCard(
        string symbolId, string fqn, string displayName, SymbolKind kind,
        string filePath, int line, string projectName, string ns)
    {
        var stableId = FSharpSymbolMapper.ComputeFSharpStableId(symbolId, kind, projectName);
        return new SymbolCard(
            SymbolId: SymbolId.From(symbolId),
            FullyQualifiedName: fqn,
            Kind: kind,
            Signature: $"internal {kind.ToString().ToLowerInvariant()} {displayName}",
            Documentation: null,
            Namespace: ns,
            ContainingType: null,
            FilePath: FilePath.From(filePath),
            SpanStart: line,
            SpanEnd: line,
            Visibility: "internal",
            CallsTop: [],
            Facts: [],
            SideEffects: [],
            ThrownExceptions: [],
            Evidence: [],
            Confidence: Confidence.Low,
            StableId: stableId);
    }

    [GeneratedRegex(@"^module\s+(\w+)")]
    private static partial Regex ModuleRegex();

    [GeneratedRegex(@"^type\s+(\w+)")]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^let\s+(\w+)")]
    private static partial Regex LetRegex();
}
