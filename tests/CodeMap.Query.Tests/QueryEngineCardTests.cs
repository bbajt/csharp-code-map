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

public class QueryEngineCardTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));
    private static readonly SymbolId SymId = SymbolId.From("NS.MyClass");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineCardTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
    }

    [Fact]
    public async Task GetCard_ExistingSymbol_ReturnsSymbolCard()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        var result = await _engine.GetSymbolCardAsync(Routing, SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.FullyQualifiedName.Should().Be("NS.MyClass");
    }

    [Fact]
    public async Task GetCard_ExistingSymbol_EnvelopeHasAnswer()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        var result = await _engine.GetSymbolCardAsync(Routing, SymId);

        result.Value.Answer.Should().Contain("NS.MyClass");
        result.Value.Answer.Should().Contain("Class");
    }

    [Fact]
    public async Task GetCard_ExistingSymbol_NextActionsSuggestDefinitionSpan()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        var result = await _engine.GetSymbolCardAsync(Routing, SymId);

        result.Value.NextActions.Should().Contain(a => a.Tool == "symbols.get_definition_span");
    }

    [Fact]
    public async Task GetCard_ExistingSymbol_EvidencePointsToSourceLocation()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        var result = await _engine.GetSymbolCardAsync(Routing, SymId);

        result.Value.Evidence.Should().HaveCount(1);
        result.Value.Evidence[0].FilePath.Value.Should().Be("src/MyClass.cs");
        result.Value.Evidence[0].LineStart.Should().Be(10);
    }

    [Fact]
    public async Task GetCard_NonExistentSymbol_ReturnsNotFound()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns((SymbolCard?)null);

        var result = await _engine.GetSymbolCardAsync(Routing, SymId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetCard_NoBaseline_ReturnsIndexNotAvailable()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(false);

        var result = await _engine.GetSymbolCardAsync(Routing, SymId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task GetCard_CacheHitOnSecondCall()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        await _engine.GetSymbolCardAsync(Routing, SymId);
        await _engine.GetSymbolCardAsync(Routing, SymId);

        await _store.Received(1).GetSymbolAsync(Repo, Sha, SymId);
    }

    [Fact]
    public async Task GetCard_TracksTokenSavings()
    {
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        await _engine.GetSymbolCardAsync(Routing, SymId);

        _tracker.Received(1).RecordSaving(
            Arg.Is<int>(n => n > 0),
            Arg.Any<Dictionary<string, decimal>>());
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
            confidence: Confidence.High);
}
