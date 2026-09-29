namespace CodeMap.Mcp.Tests;

using System.Text.RegularExpressions;
using CodeMap.Core.Models;
using FluentAssertions;

/// <summary>
/// Guard for PHASE-21-09 (ADR-051): no deprecated dotted tool name may appear in <c>src/</c>, neither
/// in strings (next_actions, hints, errors, descriptions) nor in <c>///</c> docs, which CodeMap indexes
/// and shows to agents. Excluded: plain <c>//</c> comments (may cite history) and the alias table in
/// <c>ToolNames.cs</c>.
/// </summary>
public sealed class ToolNameUsageGuardTests
{
    [Fact]
    public void SourceTree_HasNoDottedToolNames()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var pattern = new Regex(
            @"(?<![\w.])(" + string.Join("|", ToolNames.LegacyAliases.Keys.Select(Regex.Escape)) + @")(?![\w])",
            RegexOptions.Compiled);
        var toolNamesFile = Path.Combine("CodeMap.Core", "Models", "ToolNames.cs");

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj") || rel == toolNamesFile)
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("///", StringComparison.Ordinal))
                    continue;
                foreach (Match m in pattern.Matches(lines[i]))
                    hits.Add($"src/{rel.Replace('\\', '/')}:{i + 1}: {m.Value}");
            }
        }

        hits.Should().BeEmpty(
            $"tool names must come from ToolNames (canonical). {hits.Count} dotted name(s):\n" + string.Join("\n", hits));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CodeMap.sln not found above the test output");
    }
}
