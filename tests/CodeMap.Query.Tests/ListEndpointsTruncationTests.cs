namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Pins the truncated-flag semantics of <see cref="QueryEngine.ListEndpointsAsync"/>
/// after the M19.x.1 fix. Regression for the corpus-sweep finding where
/// <c>http_method=PAGE</c> with <c>limit=10</c> returned 8 items but
/// <c>truncated: true</c>: <see cref="QueryEngine.BuildEndpoints"/> applies the
/// filter post-fetch, so a pre-filter <c>stored.Count &gt; limit</c> doesn't
/// imply the user-visible result was truncated.
/// </summary>
public sealed class ListEndpointsTruncationTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("trunc-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public ListEndpointsTruncationTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static StoredFact Route(string value) =>
        new(SymbolId: SymbolId.From("T:Test"),
            StableId: null,
            Kind: FactKind.Route,
            Value: value,
            FilePath: FilePath.From("Test.cs"),
            LineStart: 1,
            LineEnd: 1,
            Confidence: Confidence.High);

    [Fact]
    public async Task PageFilter_FewerMatchesThanLimit_NotTruncated()
    {
        // Storage has 11 raw Route facts (limit+1) — store would normally signal
        // "more available". 8 are PAGE, 3 are GET. After the PAGE filter, 8 are
        // returned. The user sees 8 of 8 PAGE routes, which is NOT truncated.
        var facts = new List<StoredFact>
        {
            Route("PAGE /a"), Route("PAGE /b"), Route("PAGE /c"), Route("PAGE /d"),
            Route("PAGE /e"), Route("PAGE /f"), Route("PAGE /g"), Route("PAGE /h"),
            Route("GET /api/x"), Route("GET /api/y"), Route("GET /api/z"),
        };
        _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 11, Arg.Any<CancellationToken>())
              .Returns(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: "PAGE", limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(8);
        data.Truncated.Should().BeFalse(
            "the filter dropped raw facts below the limit — the user got every match");
    }

    [Fact]
    public async Task NoFilter_FetchExceedsLimit_Truncated()
    {
        // Pre-filter stored count = limit + 1 = 11. No filter, so we return
        // limit (10) and truncate. Truncated must be true.
        var facts = Enumerable.Range(0, 11).Select(i => Route($"GET /a{i}")).ToList();
        _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 11, Arg.Any<CancellationToken>())
              .Returns(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: null, limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(10);
        data.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task PageFilter_ExactlyLimitMatches_AndMoreRawAvailable_Truncated()
    {
        // 11 raw facts (limit+1), 10 of which are PAGE. After filter we return
        // exactly limit=10 PAGE matches AND there's at least one more raw fact
        // beyond — could be more PAGE matches in the next page. Truncated.
        var facts = new List<StoredFact>();
        for (int i = 0; i < 10; i++) facts.Add(Route($"PAGE /p{i}"));
        facts.Add(Route("GET /api/z"));
        _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 11, Arg.Any<CancellationToken>())
              .Returns(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: "PAGE", limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(10);
        data.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task NoFilter_FetchAtOrBelowLimit_NotTruncated()
    {
        var facts = new List<StoredFact> { Route("GET /a"), Route("GET /b"), Route("GET /c") };
        _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 11, Arg.Any<CancellationToken>())
              .Returns(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: null, limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(3);
        data.Truncated.Should().BeFalse();
    }
}
