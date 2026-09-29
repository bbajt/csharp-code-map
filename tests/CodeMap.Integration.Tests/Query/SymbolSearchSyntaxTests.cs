namespace CodeMap.Integration.Tests.Query;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// PHASE-21-08 T01, end to end on the Roslyn-indexed SampleSolution: the <c>symbols.search</c> syntax
/// the tool description promises (<c>OR</c>, quoted words) works through the real query stack. Before
/// the fix, every <c>OR</c> query and every quoted query returned 0 hits on the v2 engine.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SymbolSearchSyntaxTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    /// <summary>Uses the shared indexed SampleSolution.</summary>
    public SymbolSearchSyntaxTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    [Fact]
    public async Task Search_OrOfTwoClasses_ReturnsBoth()
    {
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            _f.CommittedRouting(), "OrderService OR AuditableEntity",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]), null, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Select(h => h.SymbolId.Value)
            .Should().Contain(id => id.EndsWith(".OrderService"))
            .And.Contain(id => id.EndsWith(".AuditableEntity"));
    }

    [Fact]
    public async Task Search_QuotedWords_MatchLikeUnquoted()
    {
        var ct = TestContext.Current.CancellationToken;
        var quoted = await _f.QueryEngine.SearchSymbolsAsync(
            _f.CommittedRouting(), "\"Order Service\"", null, null, ct);
        var unquoted = await _f.QueryEngine.SearchSymbolsAsync(
            _f.CommittedRouting(), "Order Service", null, null, ct);

        unquoted.Value.Data.Hits.Should().NotBeEmpty();
        quoted.Value.Data.Hits.Select(h => h.SymbolId).Should().Equal(unquoted.Value.Data.Hits.Select(h => h.SymbolId));
    }
}
