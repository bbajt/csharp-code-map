namespace CodeMap.Mcp.Tests;

using FluentAssertions;

/// <summary>
/// Guard for PHASE-21-13 T02: boolean tool arguments are read with <c>JsonArgs.GetBool</c>, which
/// accepts <c>true</c> and <c>"true"</c>. <c>GetValue&lt;bool&gt;()</c> throws on a JSON string, which
/// some clients send, and the boundary then reports <c>INTERNAL_ERROR</c>.
/// </summary>
public sealed class BoolArgumentGuardTests
{
    [Fact]
    public void McpSource_HasNoGetValueBool_OutsideJsonArgs()
    {
        var mcp = Path.Combine(FindRepoRoot(), "src", "CodeMap.Mcp");
        var jsonArgs = Path.Combine("Handlers", "JsonArgs.cs");

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(mcp, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(mcp, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj") || rel == jsonArgs)
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("GetValue<bool>", StringComparison.Ordinal))
                    hits.Add($"src/CodeMap.Mcp/{rel.Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
            }
        }

        hits.Should().BeEmpty(
            $"read boolean arguments with args.GetBool(key, default). {hits.Count} site(s):\n" + string.Join("\n", hits));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CodeMap.sln not found above the test output");
    }
}
