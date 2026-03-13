namespace CodeMap.Query.Tests;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class VirtualFileResolverTests
{
    private static readonly FilePath FileA = FilePath.From("src/FileA.cs");
    private static readonly FilePath FileB = FilePath.From("src/FileB.cs");

    private static VirtualFile MakeVf(FilePath path, string content) =>
        new(path, content);

    // ── Resolve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_MatchingPath_ReturnsVirtualContent()
    {
        var vf = new List<VirtualFile> { MakeVf(FileA, "line1\nline2\nline3") };

        var result = VirtualFileResolver.Resolve(FileA, vf);

        result.Should().Be("line1\nline2\nline3");
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
    {
        var vf = new List<VirtualFile> { MakeVf(FileA, "content") };

        var result = VirtualFileResolver.Resolve(FileB, vf);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_NullVirtualFiles_ReturnsNull()
    {
        var result = VirtualFileResolver.Resolve(FileA, null);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_EmptyList_ReturnsNull()
    {
        var result = VirtualFileResolver.Resolve(FileA, []);

        result.Should().BeNull();
    }

    // ── ResolveLines ──────────────────────────────────────────────────────────

    [Fact]
    public void ResolveLines_ValidRange_ReturnsLines()
    {
        var content = "line1\nline2\nline3\nline4\nline5";
        var vf = new List<VirtualFile> { MakeVf(FileA, content) };

        var result = VirtualFileResolver.ResolveLines(FileA, vf, startLine: 2, endLine: 4);

        result.Should().Be("line2\nline3\nline4");
    }

    [Fact]
    public void ResolveLines_RangeBeyondContent_ReturnsAvailableLines()
    {
        var content = "line1\nline2";
        var vf = new List<VirtualFile> { MakeVf(FileA, content) };

        var result = VirtualFileResolver.ResolveLines(FileA, vf, startLine: 1, endLine: 100);

        result.Should().Be("line1\nline2");
    }

    [Fact]
    public void ResolveLines_FileNotInVirtualFiles_ReturnsNull()
    {
        var vf = new List<VirtualFile> { MakeVf(FileA, "content") };

        var result = VirtualFileResolver.ResolveLines(FileB, vf, startLine: 1, endLine: 1);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveLines_SingleLine_ReturnsOneLine()
    {
        var content = "line1\nline2\nline3";
        var vf = new List<VirtualFile> { MakeVf(FileA, content) };

        var result = VirtualFileResolver.ResolveLines(FileA, vf, startLine: 2, endLine: 2);

        result.Should().Be("line2");
    }

    // ── BuildSpan ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSpan_MatchingFile_ReturnsSpanWithLineNumbers()
    {
        var content = "alpha\nbeta\ngamma";
        var vf = new List<VirtualFile> { MakeVf(FileA, content) };

        var span = VirtualFileResolver.BuildSpan(FileA, vf, startLine: 1, endLine: 2);

        span.Should().NotBeNull();
        span!.FilePath.Should().Be(FileA);
        span.StartLine.Should().Be(1);
        span.EndLine.Should().Be(2);
        span.Content.Should().Contain("alpha");
        span.Content.Should().Contain("beta");
    }

    [Fact]
    public void BuildSpan_FileNotVirtual_ReturnsNull()
    {
        var vf = new List<VirtualFile> { MakeVf(FileA, "content") };

        var span = VirtualFileResolver.BuildSpan(FileB, vf, startLine: 1, endLine: 2);

        span.Should().BeNull();
    }
}
