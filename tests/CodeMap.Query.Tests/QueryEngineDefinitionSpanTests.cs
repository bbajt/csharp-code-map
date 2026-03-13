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

public class QueryEngineDefinitionSpanTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));
    private static readonly SymbolId SymId = SymbolId.From("NS.Service");
    private static readonly FilePath File = FilePath.From("src/Service.cs");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineDefinitionSpanTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
    }

    [Fact]
    public async Task GetDefinitionSpan_ExistingSymbol_ReturnsSourceCode()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(10, 30));
        _store.GetFileSpanAsync(Repo, Sha, File, Arg.Any<int>(), Arg.Any<int>())
              .Returns(MakeSpan(10, 30));

        var result = await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Be("source code");
    }

    [Fact]
    public async Task GetDefinitionSpan_ExistingSymbol_AnswerIncludesSymbolName()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(10, 30));
        _store.GetFileSpanAsync(Repo, Sha, File, Arg.Any<int>(), Arg.Any<int>())
              .Returns(MakeSpan(10, 30));

        var result = await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 0);

        result.Value.Answer.Should().Contain("NS.Service");
        result.Value.Answer.Should().Contain("Definition of");
    }

    [Fact]
    public async Task GetDefinitionSpan_LongDefinition_ClampsToMaxLines()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(1, 500)); // 500-line definition
        _store.GetFileSpanAsync(Repo, Sha, File, 1, 50) // clamped to maxLines=50
              .Returns(MakeSpan(1, 50));

        var result = await _engine.GetDefinitionSpanAsync(Routing, SymId, 50, 0);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).GetFileSpanAsync(Repo, Sha, File, 1, 50);
    }

    [Fact]
    public async Task GetDefinitionSpan_WithContextLines_ExpandsAroundDefinition()
    {
        // Symbol at lines 10-20, contextLines=3 → effectiveStart=7, effectiveEnd=23
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(10, 20));
        _store.GetFileSpanAsync(Repo, Sha, File, 7, 23).Returns(MakeSpan(7, 23));

        var result = await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 3);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).GetFileSpanAsync(Repo, Sha, File, 7, 23);
    }

    [Fact]
    public async Task GetDefinitionSpan_NonExistentSymbol_ReturnsNotFound()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns((SymbolCard?)null);

        var result = await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetDefinitionSpan_NoBaseline_ReturnsIndexNotAvailable()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(false);

        var result = await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task GetDefinitionSpan_CacheHitOnRepeatCall()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(10, 30));
        _store.GetFileSpanAsync(Repo, Sha, File, Arg.Any<int>(), Arg.Any<int>())
              .Returns(MakeSpan(10, 30));

        await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 0);
        await _engine.GetDefinitionSpanAsync(Routing, SymId, 200, 0);

        // symbol store and file span should each be called once
        await _store.Received(1).GetSymbolAsync(Repo, Sha, SymId);
        await _store.Received(1).GetFileSpanAsync(Repo, Sha, File, Arg.Any<int>(), Arg.Any<int>());
    }

    // ─── Factory ─────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(int spanStart, int spanEnd) =>
        SymbolCard.CreateMinimal(
            symbolId: SymId,
            fullyQualifiedName: "NS.Service",
            kind: SymbolKind.Class,
            signature: "Service()",
            @namespace: "NS",
            filePath: File,
            spanStart: spanStart,
            spanEnd: spanEnd,
            visibility: "public",
            confidence: Confidence.High);

    private static FileSpan MakeSpan(int start, int end) =>
        new(File, start, end, 300, "source code", false);
}
