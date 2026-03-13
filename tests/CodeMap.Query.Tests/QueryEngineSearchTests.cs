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

public class QueryEngineSearchTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));

    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);
    private static readonly RoutingContext NoShaRouting = new(Repo); // no BaselineCommitSha

    public QueryEngineSearchTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
    }

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ValidQuery_ReturnsEnvelopeWithHits()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Order", null, Arg.Any<int>())
              .Returns(MakeHits(3));

        var result = await _engine.SearchSymbolsAsync(Routing, "Order", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_ValidQuery_EnvelopeHasAnswer()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Order", null, Arg.Any<int>())
              .Returns(MakeHits(2));

        var result = await _engine.SearchSymbolsAsync(Routing, "Order", null, null);

        result.Value.Answer.Should().Contain("Order");
        result.Value.Answer.Should().Contain("2");
    }

    [Fact]
    public async Task Search_ValidQuery_EnvelopeHasNextActions()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, Arg.Any<int>())
              .Returns(MakeHits(5));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, null);

        result.Value.NextActions.Should().HaveCount(3); // top 3
        result.Value.NextActions.Should().AllSatisfy(a => a.Tool.Should().Be("symbols.get_card"));
    }

    [Fact]
    public async Task Search_ValidQuery_EnvelopeHasTimingMetadata()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, Arg.Any<int>())
              .Returns(MakeHits(1));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, null);

        result.Value.Meta.Timing.TotalMs.Should().BeGreaterThanOrEqualTo(0);
        result.Value.Meta.BaselineCommitSha.Should().Be(Sha);
    }

    // ─── Budget enforcement ───────────────────────────────────────────────────

    [Fact]
    public async Task Search_DefaultBudget_LimitsTo20Results()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, 21) // 20 + 1 for truncation detection
              .Returns(MakeHits(10));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Search_CustomBudget_HonorsRequestedLimit()
    {
        var budgets = new BudgetLimits(maxResults: 5);
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, 6)
              .Returns(MakeHits(3));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, budgets);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_BudgetExceedingHardCap_ClampsToHardCap()
    {
        var budgets = new BudgetLimits(maxResults: 200); // hard cap is 100
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, 101)
              .Returns(MakeHits(10));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, budgets);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Search_BudgetExceedingHardCap_ReportsLimitsApplied()
    {
        var budgets = new BudgetLimits(maxResults: 200); // hard cap is 100
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, Arg.Any<int>())
              .Returns(MakeHits(5));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, budgets);

        result.Value.Meta.LimitsApplied.Should().ContainKey("MaxResults");
        result.Value.Meta.LimitsApplied["MaxResults"].Requested.Should().Be(200);
        result.Value.Meta.LimitsApplied["MaxResults"].HardCap.Should().Be(100);
    }

    // ─── Truncation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_MoreResultsThanLimit_TruncatedTrue()
    {
        var budgets = new BudgetLimits(maxResults: 3);
        // Return 4 hits (3+1) — triggers truncation
        _store.SearchSymbolsAsync(Repo, Sha, "X", null, 4)
              .Returns(MakeHits(4));

        var result = await _engine.SearchSymbolsAsync(Routing, "X", null, budgets);

        result.Value.Data.Truncated.Should().BeTrue();
        result.Value.Data.Hits.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_FewerResultsThanLimit_TruncatedFalse()
    {
        var budgets = new BudgetLimits(maxResults: 10);
        _store.SearchSymbolsAsync(Repo, Sha, "X", null, 11)
              .Returns(MakeHits(3));

        var result = await _engine.SearchSymbolsAsync(Routing, "X", null, budgets);

        result.Value.Data.Truncated.Should().BeFalse();
    }

    // ─── Caching ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_SameQueryTwice_SecondCallHitsCache()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Order", null, Arg.Any<int>())
              .Returns(MakeHits(2));

        await _engine.SearchSymbolsAsync(Routing, "Order", null, null);
        await _engine.SearchSymbolsAsync(Routing, "Order", null, null);

        // Store should only be called once (second call hits cache)
        await _store.Received(1).SearchSymbolsAsync(Repo, Sha, "Order", null, Arg.Any<int>());
    }

    [Fact]
    public async Task Search_DifferentQuery_NoCacheHit()
    {
        _store.SearchSymbolsAsync(Repo, Sha, Arg.Any<string>(), null, Arg.Any<int>())
              .Returns(MakeHits(1));

        await _engine.SearchSymbolsAsync(Routing, "Alpha", null, null);
        await _engine.SearchSymbolsAsync(Routing, "Beta", null, null);

        await _store.Received(2).SearchSymbolsAsync(Repo, Sha, Arg.Any<string>(), null, Arg.Any<int>());
    }

    // ─── Token savings ────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_TracksTokenSavings()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, Arg.Any<int>())
              .Returns(MakeHits(5));

        await _engine.SearchSymbolsAsync(Routing, "Foo", null, null);

        _tracker.Received(1).RecordSaving(
            Arg.Is<int>(n => n > 0),
            Arg.Any<Dictionary<string, decimal>>());
    }

    [Fact]
    public async Task Search_TokensSavedInMeta()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, Arg.Any<int>())
              .Returns(MakeHits(5));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, null);

        result.Value.Meta.TokensSaved.Should().BeGreaterThan(0);
    }

    // ─── Error cases ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_EmptyOrWhitespaceQuery_ReturnsInvalidArgument(string query)
    {
        var result = await _engine.SearchSymbolsAsync(Routing, query, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Search_NullQuery_ReturnsInvalidArgument()
    {
        var result = await _engine.SearchSymbolsAsync(Routing, null!, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Search_NoBaseline_ReturnsIndexNotAvailable()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(false);

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task Search_NoCommitSha_ReturnsIndexNotAvailable()
    {
        var result = await _engine.SearchSymbolsAsync(NoShaRouting, "Foo", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    // ─── Filters ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_WithFilters_PassesToStorage()
    {
        var filters = new SymbolSearchFilters(
            Kinds: [SymbolKind.Class],
            Namespace: "MyNs");

        _store.SearchSymbolsAsync(Repo, Sha, "Foo", filters, Arg.Any<int>())
              .Returns(MakeHits(1));

        var result = await _engine.SearchSymbolsAsync(Routing, "Foo", filters, null);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).SearchSymbolsAsync(Repo, Sha, "Foo", filters, Arg.Any<int>());
    }

    // ─── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ZeroResults_ReturnsEmptyHitsWithAnswer()
    {
        _store.SearchSymbolsAsync(Repo, Sha, "unknown", null, Arg.Any<int>())
              .Returns(new List<SymbolSearchHit>());

        var result = await _engine.SearchSymbolsAsync(Routing, "unknown", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().BeEmpty();
        result.Value.Answer.Should().Contain("No symbols found");
    }

    [Fact]
    public async Task Search_CancellationRequested_PropagatesToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Use Arg matchers for all parameters so NSubstitute matches regardless of CT value
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
              .Returns(true);
        _store.SearchSymbolsAsync(
                  Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<string>(),
                  Arg.Any<SymbolSearchFilters?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(callInfo =>
                  Task.FromCanceled<IReadOnlyList<SymbolSearchHit>>(callInfo.Arg<CancellationToken>()));

        var act = async () => await _engine.SearchSymbolsAsync(Routing, "Foo", null, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── Factory helpers ──────────────────────────────────────────────────────

    private static IReadOnlyList<SymbolSearchHit> MakeHits(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new SymbolSearchHit(
                SymbolId.From($"NS.Class{i}"),
                $"NS.Class{i}",
                SymbolKind.Class,
                $"Class{i}()",
                null,
                FilePath.From($"src/Class{i}.cs"),
                i,
                1.0 / i))
            .ToList();
}
