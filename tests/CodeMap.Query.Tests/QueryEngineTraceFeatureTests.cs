namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class QueryEngineTraceFeatureTests
{
    private static readonly RepoId Repo = RepoId.From("qe-trace-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));

    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly GraphTraverser _traverser = new();
    private readonly FeatureTracer _featureTracer;
    private readonly QueryEngine _engine;

    public QueryEngineTraceFeatureTests()
    {
        _featureTracer = new FeatureTracer(_store, _traverser);
        _engine = new QueryEngine(
            _store,
            _cache,
            _tracker,
            new ExcerptReader(_store),
            _traverser,
            _featureTracer,
            NullLogger<QueryEngine>.Instance);

        // Baseline always exists
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);

        // Cache returns null by default (miss)
        _cache.GetAsync<ResponseEnvelope<FeatureTraceResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((ResponseEnvelope<FeatureTraceResponse>?)null);
    }

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static SymbolCard MakeCard(string id) =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(id),
            fullyQualifiedName: id,
            kind: SymbolKind.Method,
            signature: id,
            @namespace: "Ns",
            filePath: FilePath.From("A.cs"),
            spanStart: 1,
            spanEnd: 5,
            visibility: "public",
            confidence: Confidence.High);

    [Fact]
    public async Task TraceFeature_CommittedMode_ReturnsSuccessEnvelope()
    {
        var symbolId = SymbolId.From("A::Method");
        _store.GetSymbolAsync(Repo, Sha, symbolId, Arg.Any<CancellationToken>())
              .Returns(MakeCard("A::Method"));
        _store.GetOutgoingReferencesAsync(Repo, Sha, symbolId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredOutgoingReference>());
        _store.GetFactsForSymbolAsync(Repo, Sha, symbolId, Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredFact>());

        var result = await _engine.TraceFeatureAsync(CommittedRouting(), symbolId, depth: 1, limit: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.EntryPoint.Should().Be(symbolId);
        result.Value.Data.Nodes.Should().HaveCount(1);
    }

    [Fact]
    public async Task TraceFeature_CacheHit_ReturnsCachedResult()
    {
        var symbolId = SymbolId.From("A::CachedMethod");
        var meta = new ResponseMeta(
            Timing: new TimingBreakdown(0, 0, 0),
            BaselineCommitSha: Sha,
            LimitsApplied: new Dictionary<string, LimitApplied>(),
            TokensSaved: 0,
            CostAvoided: 0);
        var cachedEnvelope = new ResponseEnvelope<FeatureTraceResponse>(
            Answer: "cached answer",
            Data: new FeatureTraceResponse(symbolId, "CachedMethod", null, [], 1, 3, false),
            Evidence: [],
            NextActions: [],
            Confidence: Confidence.High,
            Meta: meta);

        _cache.GetAsync<ResponseEnvelope<FeatureTraceResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(cachedEnvelope);

        var result = await _engine.TraceFeatureAsync(CommittedRouting(), symbolId, depth: 3, limit: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.Answer.Should().Be("cached answer", "cache hit should return cached answer");
        await _store.DidNotReceive().GetSymbolAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TraceFeature_SymbolNotFound_ReturnsNotFoundError()
    {
        var symbolId = SymbolId.From("NonExistent::Method");
        _store.GetSymbolAsync(Repo, Sha, symbolId, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.TraceFeatureAsync(CommittedRouting(), symbolId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(Core.Errors.ErrorCodes.NotFound);
    }

    [Fact]
    public async Task TraceFeature_ResponseEnvelope_HasCorrectMetadata()
    {
        var symbolId = SymbolId.From("B::Run");
        _store.GetSymbolAsync(Repo, Sha, symbolId, Arg.Any<CancellationToken>())
              .Returns(MakeCard("B::Run"));
        _store.GetOutgoingReferencesAsync(Repo, Sha, symbolId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredOutgoingReference>());
        _store.GetFactsForSymbolAsync(Repo, Sha, symbolId, Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredFact>());

        var result = await _engine.TraceFeatureAsync(CommittedRouting(), symbolId, depth: 2, limit: 10);

        result.IsSuccess.Should().BeTrue();
        result.Value.Answer.Should().Contain("B::Run");
        result.Value.Meta.BaselineCommitSha.Should().Be(Sha);
    }
}
