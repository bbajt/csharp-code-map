namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Pins the filter-before-limit contract for
/// <see cref="QueryEngine.ListConfigKeysAsync"/> — the same class of bug as the
/// reported endpoints defect (GH #6). Config facts are stored ordered by value,
/// so a keyFilter match that sorts after a limit-sized fetch window was invisible
/// before the fix.
/// </summary>
public sealed class ListConfigKeysTruncationTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("cfg-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public ListConfigKeysTruncationTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static StoredFact Config(string value) =>
        new(SymbolId: SymbolId.From("T:Test"),
            StableId: null,
            Kind: FactKind.Config,
            Value: value,
            FilePath: FilePath.From("Test.cs"),
            LineStart: 1,
            LineEnd: 1,
            Confidence: Confidence.High);

    private void GivenConfigFacts(IEnumerable<StoredFact> facts)
    {
        var ordered = facts.OrderBy(f => f.Value, StringComparer.Ordinal).ToList();
        _store.GetFactsByKindAsync(Repo, Sha, FactKind.Config, int.MaxValue, Arg.Any<CancellationToken>())
              .Returns(ordered);
    }

    [Fact]
    public async Task KeyFilter_MatchesSortAfterAlphabeticalWindow_AllReturned()
    {
        // 60 "App:" keys + 4 "Zed:" keys at the default limit of 50. "Zed" sorts
        // after "App", so the pre-fix limit+1 window never contained a Zed key and
        // key_filter:"Zed:" returned zero. Filter-before-limit surfaces all four.
        var facts = new List<StoredFact>();
        for (int i = 0; i < 60; i++) facts.Add(Config($"App:Setting{i:00}|GetValue"));
        for (int i = 0; i < 4; i++) facts.Add(Config($"Zed:Flag{i:00}|GetValue"));
        GivenConfigFacts(facts);

        var result = await _engine.ListConfigKeysAsync(Routing, keyFilter: "Zed:", limit: 50);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Keys.Should().HaveCount(4, "all Zed keys must surface despite sorting after the App block");
        data.Keys.Should().OnlyContain(k => k.Key.StartsWith("Zed:"));
        data.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task NoFilter_MatchesExceedLimit_Truncated()
    {
        GivenConfigFacts(Enumerable.Range(0, 60).Select(i => Config($"App:S{i:00}|GetValue")));

        var result = await _engine.ListConfigKeysAsync(Routing, keyFilter: null, limit: 50);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Keys.Should().HaveCount(50);
        data.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task KeyFilter_FewerMatchesThanLimit_NotTruncated()
    {
        var facts = new List<StoredFact>
        {
            Config("App:A|GetValue"), Config("App:B|GetValue"),
            Config("Other:X|GetValue"), Config("Other:Y|GetValue"),
        };
        GivenConfigFacts(facts);

        var result = await _engine.ListConfigKeysAsync(Routing, keyFilter: "App:", limit: 50);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;
        data.Keys.Should().HaveCount(2);
        data.Truncated.Should().BeFalse();
    }
}
