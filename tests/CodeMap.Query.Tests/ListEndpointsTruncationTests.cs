namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Pins the filter-before-limit contract and exact truncation semantics of
/// <see cref="QueryEngine.ListEndpointsAsync"/> (GH #6).
///
/// <para>Facts are returned by the store ordered alphabetically by value
/// (<c>"METHOD /path"</c>). The pre-fix code fetched only <c>limit+1</c> facts
/// and applied the path / verb filter afterwards, so on a repo with more routes
/// than the limit the fetch window was dominated by whichever verb sorts first
/// (<c>DELETE</c>) and every other match was silently truncated. The fix fetches
/// the complete fact set, filters, then applies the limit — so truncation is
/// exact and no match is hidden.</para>
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

    /// <summary>
    /// The engine must request every Route fact (fetch-all), not a limit-sized
    /// window. Facts are supplied in store order (sorted by value) regardless.
    /// </summary>
    private void GivenRouteFacts(IEnumerable<StoredFact> facts)
    {
        var ordered = facts.OrderBy(f => f.Value, StringComparer.Ordinal).ToList();
        _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, int.MaxValue, Arg.Any<CancellationToken>())
              .Returns(ordered);
    }

    [Fact]
    public async Task PageFilter_FewerMatchesThanLimit_NotTruncated()
    {
        // 8 PAGE + 3 GET. After the PAGE filter, 8 are returned — every match — so
        // the result is NOT truncated even though the raw fact count (11) > limit.
        GivenRouteFacts(new[]
        {
            Route("PAGE /a"), Route("PAGE /b"), Route("PAGE /c"), Route("PAGE /d"),
            Route("PAGE /e"), Route("PAGE /f"), Route("PAGE /g"), Route("PAGE /h"),
            Route("GET /api/x"), Route("GET /api/y"), Route("GET /api/z"),
        });

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: "PAGE", limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(8);
        data.Truncated.Should().BeFalse(
            "the filter dropped raw facts below the limit — the user got every match");
    }

    [Fact]
    public async Task NoFilter_MatchesExceedLimit_Truncated()
    {
        GivenRouteFacts(Enumerable.Range(0, 11).Select(i => Route($"GET /a{i:00}")));

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: null, limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(10);
        data.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task PageFilter_ExactlyLimitMatches_NoMoreMatches_NotTruncated()
    {
        // 10 PAGE + 1 GET, filter PAGE, limit 10. Because filtering precedes the
        // limit and we fetched the COMPLETE set, we know there are exactly 10 PAGE
        // matches — the extra GET fact is not a hidden PAGE. NOT truncated.
        // (Pre-fix this reported truncated:true, a false positive.)
        var facts = new List<StoredFact>();
        for (int i = 0; i < 10; i++) facts.Add(Route($"PAGE /p{i:00}"));
        facts.Add(Route("GET /api/z"));
        GivenRouteFacts(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: "PAGE", limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(10);
        data.Truncated.Should().BeFalse("all 10 PAGE matches fit within the limit");
    }

    [Fact]
    public async Task PageFilter_MoreMatchesThanLimit_Truncated()
    {
        var facts = Enumerable.Range(0, 12).Select(i => Route($"PAGE /p{i:00}")).ToList();
        GivenRouteFacts(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: "PAGE", limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(10);
        data.Truncated.Should().BeTrue("12 PAGE matches, limit 10 — genuinely truncated");
    }

    [Fact]
    public async Task NoFilter_MatchesAtOrBelowLimit_NotTruncated()
    {
        GivenRouteFacts(new[] { Route("GET /a"), Route("GET /b"), Route("GET /c") });

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: null, limit: 10);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(3);
        data.Truncated.Should().BeFalse();
    }

    // ─── GH #6 regression ─────────────────────────────────────────────────────

    [Fact]
    public async Task VerbFilter_MatchesSortAfterAlphabeticalWindow_AllReturned()
    {
        // The exact GH #6 shape: 60 DELETE routes + 6 GET routes at the default
        // limit of 50. DELETE sorts before GET, so the pre-fix limit+1 fetch window
        // (51 facts) was entirely DELETE and http_method:GET returned ZERO. With
        // filter-before-limit every GET match must surface.
        var facts = new List<StoredFact>();
        for (int i = 0; i < 60; i++) facts.Add(Route($"DELETE /api/thing/{i:00}"));
        for (int i = 0; i < 6; i++) facts.Add(Route($"GET /api/thing/{i:00}"));
        GivenRouteFacts(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: null, httpMethod: "GET", limit: 50);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(6, "all GET routes must surface despite sorting after the DELETE block");
        data.Endpoints.Should().OnlyContain(e => e.HttpMethod == "GET");
        data.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task PathFilter_MixedVerbsBehindLargeDeleteBlock_AllVerbsReturned()
    {
        // A ChatController with all verbs, sitting behind a large block of DELETE
        // routes from elsewhere. path_filter must return every /api/chat action,
        // not just the DELETE one (the reporter's observed symptom).
        var facts = new List<StoredFact>();
        for (int i = 0; i < 60; i++) facts.Add(Route($"DELETE /api/other/{i:00}"));
        facts.Add(Route("POST /api/chat/send"));
        facts.Add(Route("GET /api/chat/messages"));
        facts.Add(Route("POST /api/chat/close"));
        facts.Add(Route("GET /api/chat/sessions"));
        facts.Add(Route("POST /api/chat/reply/{sessionId}"));
        facts.Add(Route("DELETE /api/chat/delete/{sessionId}"));
        facts.Add(Route("POST /api/chat/contact"));
        GivenRouteFacts(facts);

        var result = await _engine.ListEndpointsAsync(Routing, pathFilter: "/api/chat", httpMethod: null, limit: 50);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Endpoints.Should().HaveCount(7, "every /api/chat endpoint must return regardless of verb");
        data.Endpoints.Select(e => e.HttpMethod).Distinct()
            .Should().BeEquivalentTo(new[] { "POST", "GET", "DELETE" });
        data.Truncated.Should().BeFalse();
    }
}
