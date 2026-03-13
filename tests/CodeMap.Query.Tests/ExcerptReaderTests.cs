namespace CodeMap.Query.Tests;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using FluentAssertions;
using NSubstitute;

public class ExcerptReaderTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ExcerptReader _reader;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly FilePath File = FilePath.From("src/Service.cs");

    public ExcerptReaderTests()
    {
        _reader = new ExcerptReader(_store);
    }

    [Fact]
    public async Task ReadLine_ValidFile_ReturnsLine()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, 5, 5, Arg.Any<CancellationToken>())
              .Returns(MakeSpan("    public void Foo() {}"));

        var result = await _reader.ReadLineAsync(Repo, Sha, File, 5);

        result.Should().Be("public void Foo() {}");
    }

    [Fact]
    public async Task ReadLine_TrimWhitespace()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, 3, 3, Arg.Any<CancellationToken>())
              .Returns(MakeSpan("   var x = 1;   "));

        var result = await _reader.ReadLineAsync(Repo, Sha, File, 3);

        result.Should().Be("var x = 1;");
    }

    [Fact]
    public async Task ReadLine_FileNotExists_ReturnsNull()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, 1, 1, Arg.Any<CancellationToken>())
              .Returns((FileSpan?)null);

        var result = await _reader.ReadLineAsync(Repo, Sha, File, 1);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadLine_LineOutOfRange_ReturnsNull()
    {
        var result = await _reader.ReadLineAsync(Repo, Sha, File, 0);

        result.Should().BeNull();
        await _store.DidNotReceive().GetFileSpanAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<FilePath>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadLine_LongLine_TruncatedTo200()
    {
        var longLine = new string('x', 300);
        _store.GetFileSpanAsync(Repo, Sha, File, 1, 1, Arg.Any<CancellationToken>())
              .Returns(MakeSpan(longLine));

        var result = await _reader.ReadLineAsync(Repo, Sha, File, 1);

        result!.Length.Should().Be(200);
        result.Should().EndWith("...");
    }

    private static FileSpan MakeSpan(string content) =>
        new(File, 1, 1, 100, content, false);
}
