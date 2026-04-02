namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// VB.NET regression: symbol search, get_card, FTS CamelCase tokenisation.
/// All tests run against IndexedSampleVbSolutionFixture (one compilation shared).
/// </summary>
[Trait("Category", "Integration")]
[Collection("VbRegression")]
public sealed class VbSymbolQueryTests(IndexedSampleVbSolutionFixture fixture)
{
    // ── Symbol search ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchByName_OrderClass_Found()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Order",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("Order") && h.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task SearchByKind_InterfaceBrowse_ReturnsInterfaces()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), null,
            new SymbolSearchFilters(Kinds: [SymbolKind.Interface]),
            new BudgetLimits(maxResults: 20));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("IOrderService"));
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("IEntity"));
    }

    [Fact]
    public async Task GetCard_SubmitOrderAsync_ReturnsCard()
    {
        var searchResult = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "SubmitOrderAsync",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));

        searchResult.IsSuccess.Should().BeTrue();
        var hit = searchResult.Value.Data.Hits.First(h =>
            h.FullyQualifiedName.Contains("SubmitOrderAsync"));

        var card = await fixture.QueryEngine.GetSymbolCardAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        card.IsSuccess.Should().BeTrue();
        card.Value.Data.FullyQualifiedName.Should().Contain("SubmitOrderAsync");
        card.Value.Data.Kind.Should().Be(SymbolKind.Method);
    }

    [Fact]
    public async Task SearchByName_Calculator_ModuleIndexedAsClass()
    {
        // VB.NET Modules map to SymbolKind.Class (TypeKind.Module → catch-all)
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Calculator", null,
            new BudgetLimits(maxResults: 5));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("Calculator"));
    }

    [Fact]
    public async Task FtsSearch_CamelCase_VbMethodFound()
    {
        // "SubmitOrder" tokenises to "submit order" — FTS should find SubmitOrderAsync
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "submit order", null,
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("SubmitOrderAsync"));
    }

    [Fact]
    public async Task GetDefinitionSpan_OrderClass_ReturnsNonEmptySource()
    {
        var searchResult = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Order",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        searchResult.IsSuccess.Should().BeTrue();
        var hit = searchResult.Value.Data.Hits.First(h =>
            h.FullyQualifiedName.EndsWith(".Order") ||
            h.FullyQualifiedName == "Order");

        var span = await fixture.QueryEngine.GetDefinitionSpanAsync(
            fixture.CommittedRouting(), hit.SymbolId,
            maxLines: 50, contextLines: 0);

        span.IsSuccess.Should().BeTrue();
        span.Value.Data.Content.Should().NotBeNullOrWhiteSpace();
    }
}
