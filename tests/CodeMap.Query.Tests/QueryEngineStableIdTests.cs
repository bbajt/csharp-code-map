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

public sealed class QueryEngineStableIdTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly SymbolId SymId = SymbolId.From("NS.MyClass");
    private static readonly StableId Stable = new("sym_" + new string('a', 16));
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineStableIdTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
    }

    [Fact]
    public async Task GetSymbolByStableId_Found_ReturnsSymbolCard()
    {
        _store.GetSymbolByStableIdAsync(Repo, Sha, Stable, Arg.Any<CancellationToken>())
              .Returns(MakeCard());

        var result = await _engine.GetSymbolByStableIdAsync(Routing, Stable);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.FullyQualifiedName.Should().Be("NS.MyClass");
    }

    [Fact]
    public async Task GetSymbolByStableId_NotFound_ReturnsNotFoundError()
    {
        _store.GetSymbolByStableIdAsync(Repo, Sha, Stable, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.GetSymbolByStableIdAsync(Routing, Stable);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetSymbolByStableId_NoBaseline_ReturnsIndexNotAvailable()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(false);

        var result = await _engine.GetSymbolByStableIdAsync(Routing, Stable);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task GetSymbolByStableId_CacheHitOnSecondCall()
    {
        _store.GetSymbolByStableIdAsync(Repo, Sha, Stable, Arg.Any<CancellationToken>())
              .Returns(MakeCard());

        await _engine.GetSymbolByStableIdAsync(Routing, Stable);
        await _engine.GetSymbolByStableIdAsync(Routing, Stable);

        await _store.Received(1).GetSymbolByStableIdAsync(Repo, Sha, Stable, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSymbolByStableId_Found_EnvelopeHasAnswer()
    {
        _store.GetSymbolByStableIdAsync(Repo, Sha, Stable, Arg.Any<CancellationToken>())
              .Returns(MakeCard());

        var result = await _engine.GetSymbolByStableIdAsync(Routing, Stable);

        result.Value.Answer.Should().Contain("NS.MyClass");
        result.Value.Answer.Should().Contain("Class");
    }

    // ─── Factory ─────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard() =>
        SymbolCard.CreateMinimal(
            symbolId: SymId,
            fullyQualifiedName: "NS.MyClass",
            kind: SymbolKind.Class,
            signature: "MyClass()",
            @namespace: "NS",
            filePath: FilePath.From("src/MyClass.cs"),
            spanStart: 10,
            spanEnd: 50,
            visibility: "public",
            confidence: Confidence.High) with
        { StableId = Stable };
}
