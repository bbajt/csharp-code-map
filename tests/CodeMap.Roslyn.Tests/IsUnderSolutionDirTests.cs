namespace CodeMap.Roslyn.Tests;

using FluentAssertions;

/// <summary>
/// Bounds check on <see cref="RoslynCompiler.IsUnderSolutionDir"/> — the
/// guard that prevents a malicious <c>#pragma checksum</c> directive in
/// untrusted source from steering CodeMap into reading arbitrary files
/// outside the indexed repository (M19 fix-up).
/// </summary>
public class IsUnderSolutionDirTests : IDisposable
{
    private readonly string _root;

    public IsUnderSolutionDirTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codemap-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void PathInsideRoot_ReturnsTrue()
    {
        var inside = Path.Combine(_root, "Components", "Pages", "Counter.razor");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "");

        RoslynCompiler.IsUnderSolutionDir(inside, _root).Should().BeTrue();
    }

    [Fact]
    public void PathOutsideRoot_ReturnsFalse()
    {
        // Sibling temp dir — not under the root.
        var outside = Path.Combine(Path.GetTempPath(), "codemap-bounds-other-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "");

        try
        {
            RoslynCompiler.IsUnderSolutionDir(outside, _root).Should().BeFalse();
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void DotDotEscape_ReturnsFalse()
    {
        // Classic path-traversal vector: relative path containing `..` segments.
        var traversal = Path.Combine(_root, "..", "..", "Windows", "System32", "drivers", "etc", "hosts");
        RoslynCompiler.IsUnderSolutionDir(traversal, _root).Should().BeFalse();
    }

    [Fact]
    public void AbsoluteSystemPath_ReturnsFalse()
    {
        // Even if the file exists, an absolute path outside the root is rejected.
        RoslynCompiler.IsUnderSolutionDir(@"C:\Windows\System32\drivers\etc\hosts", _root).Should().BeFalse();
    }

    [Fact]
    public void EmptyOrWhitespace_ReturnsFalse()
    {
        RoslynCompiler.IsUnderSolutionDir("", _root).Should().BeFalse();
        RoslynCompiler.IsUnderSolutionDir("   ", _root).Should().BeFalse();
        RoslynCompiler.IsUnderSolutionDir("anything", "").Should().BeFalse();
    }

    [Fact]
    public void RootItself_ReturnsFalse()
    {
        // The root directory itself isn't a file under the root; we want files
        // strictly inside, never the root path itself.
        RoslynCompiler.IsUnderSolutionDir(_root, _root).Should().BeFalse();
    }

    [Fact]
    public void MixedSlashes_NormalisedConsistently()
    {
        // Same logical path expressed with mixed separators — should still be accepted.
        var withMixed = _root.Replace('\\', '/') + "/some/file.razor";
        RoslynCompiler.IsUnderSolutionDir(withMixed, _root).Should().BeTrue();
    }

    [Fact]
    public void CaseInsensitiveOnWindows()
    {
        // Windows file system is case-insensitive — uppercase root should match
        // lowercase candidate. Documented as Windows-only behaviour; on Linux
        // this would still match because of Ordinal.IgnoreCase per the impl.
        var candidate = Path.Combine(_root.ToUpperInvariant(), "FILE.RAZOR");
        RoslynCompiler.IsUnderSolutionDir(candidate, _root.ToLowerInvariant()).Should().BeTrue();
    }
}
