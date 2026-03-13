namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class EvidencePointerTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly FilePath Path = FilePath.From("src/Foo.cs");

    [Fact]
    public void Constructor_ValidArgs_CreatesInstance()
    {
        var ep = new EvidencePointer(Repo, Path, 1, 10);
        ep.LineStart.Should().Be(1);
        ep.LineEnd.Should().Be(10);
    }

    [Fact]
    public void Constructor_LineStartZero_Throws() =>
        FluentActions.Invoking(() => new EvidencePointer(Repo, Path, 0, 1))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Constructor_LineEndLessThanLineStart_Throws() =>
        FluentActions.Invoking(() => new EvidencePointer(Repo, Path, 5, 4))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Constructor_LineStartEqualsLineEnd_Succeeds()
    {
        var ep = new EvidencePointer(Repo, Path, 5, 5);
        ep.LineStart.Should().Be(5);
        ep.LineEnd.Should().Be(5);
    }

    [Fact]
    public void Constructor_OptionalFields_DefaultToNull()
    {
        var ep = new EvidencePointer(Repo, Path, 1, 1);
        ep.SymbolId.Should().BeNull();
        ep.Excerpt.Should().BeNull();
    }
}
