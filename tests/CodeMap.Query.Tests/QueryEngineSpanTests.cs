namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class QueryEngineSpanTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly FilePath File = FilePath.From("src/Foo.cs");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineSpanTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
    }

    [Fact]
    public async Task GetSpan_ValidRange_ReturnsContent()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, 5, 10).Returns(MakeSpan(File, 5, 10, 100, "content"));

        var result = await _engine.GetSpanAsync(Routing, File, 5, 10, 0, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Be("content");
        result.Value.Data.StartLine.Should().Be(5);
        result.Value.Data.EndLine.Should().Be(10);
    }

    [Fact]
    public async Task GetSpan_WithContextLines_ExpandsRange()
    {
        // contextLines = 2, so effectiveStart = 3, effectiveEnd = 12
        _store.GetFileSpanAsync(Repo, Sha, File, 3, 12).Returns(MakeSpan(File, 3, 12, 100, "code"));

        var result = await _engine.GetSpanAsync(Routing, File, 5, 10, 2, null);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).GetFileSpanAsync(Repo, Sha, File, 3, 12);
    }

    [Fact]
    public async Task GetSpan_ContextLinesAtFileStart_ClampsTo1()
    {
        // startLine=2, contextLines=5 → effectiveStart = max(1, 2-5) = 1
        _store.GetFileSpanAsync(Repo, Sha, File, 1, Arg.Any<int>())
              .Returns(MakeSpan(File, 1, 5, 50, "top"));

        var result = await _engine.GetSpanAsync(Routing, File, 2, 2, 5, null);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).GetFileSpanAsync(Repo, Sha, File, 1, Arg.Any<int>());
    }

    [Fact]
    public async Task GetSpan_ExceedsMaxLines_TruncatesAndReportsLimit()
    {
        var budgets = new BudgetLimits(maxLines: 10);
        // Request 1-50 (50 lines) with 0 context → exceeds maxLines=10
        _store.GetFileSpanAsync(Repo, Sha, File, 1, 10) // clamped to 10
              .Returns(MakeSpan(File, 1, 10, 100, "code"));

        var result = await _engine.GetSpanAsync(Routing, File, 1, 50, 0, budgets);

        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.LimitsApplied.Should().ContainKey("MaxLines");
    }

    [Fact]
    public async Task GetSpan_ExceedsMaxChars_TruncatesContent()
    {
        var budgets = new BudgetLimits(maxChars: 10);
        var longText = new string('x', 100);
        _store.GetFileSpanAsync(Repo, Sha, File, 1, 5)
              .Returns(MakeSpan(File, 1, 5, 50, longText));

        var result = await _engine.GetSpanAsync(Routing, File, 1, 5, 0, budgets);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Length.Should().Be(10);
        result.Value.Data.Truncated.Should().BeTrue();
        result.Value.Meta.LimitsApplied.Should().ContainKey("MaxChars");
    }

    [Fact]
    public async Task GetSpan_FileNotFound_ReturnsNotFound()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, Arg.Any<int>(), Arg.Any<int>())
              .Returns((FileSpan?)null);

        var result = await _engine.GetSpanAsync(Routing, File, 1, 5, 0, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetSpan_InvalidStartLine_ReturnsInvalidArgument()
    {
        var result = await _engine.GetSpanAsync(Routing, File, 0, 5, 0, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task GetSpan_EndBeforeStart_ReturnsInvalidArgument()
    {
        var result = await _engine.GetSpanAsync(Routing, File, 10, 5, 0, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task GetSpan_NoBaseline_ReturnsIndexNotAvailable()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(false);

        var result = await _engine.GetSpanAsync(Routing, File, 1, 5, 0, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task GetSpan_CacheHitOnRepeatCall()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, 5, 10)
              .Returns(MakeSpan(File, 5, 10, 100, "code"));

        await _engine.GetSpanAsync(Routing, File, 5, 10, 0, null);
        await _engine.GetSpanAsync(Routing, File, 5, 10, 0, null);

        await _store.Received(1).GetFileSpanAsync(Repo, Sha, File, 5, 10);
    }

    [Fact]
    public async Task GetSpan_TracksTokenSavings()
    {
        _store.GetFileSpanAsync(Repo, Sha, File, 1, 5)
              .Returns(MakeSpan(File, 1, 5, 200, "code"));

        await _engine.GetSpanAsync(Routing, File, 1, 5, 0, null);

        _tracker.Received(1).RecordSaving(Arg.Any<int>(), Arg.Any<Dictionary<string, decimal>>());
    }

    // ─── Factory ─────────────────────────────────────────────────────────────

    private static FileSpan MakeSpan(FilePath fp, int start, int end, int total, string content) =>
        new(fp, start, end, total, content, false);
}
