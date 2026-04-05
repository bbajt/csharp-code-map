namespace CodeMap.Integration.Tests.Regression.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

[Trait("Category", "Integration")]
[Collection("FSharpRegression")]
public sealed class FSharpSymbolExtractionTests(IndexedFSharpSolutionFixture fixture)
{
    [Fact]
    public async Task Search_CalculatorModule_Found()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Calculator",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("Calculator") && h.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task Search_OrderRecord_Found()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Order",
            new SymbolSearchFilters(Kinds: [SymbolKind.Record]),
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.Kind == SymbolKind.Record && h.FullyQualifiedName.Contains("Order"));
    }

    [Fact]
    public async Task Search_IGreeterInterface_Found()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "IGreeter",
            new SymbolSearchFilters(Kinds: [SymbolKind.Interface]),
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("IGreeter") && h.Kind == SymbolKind.Interface);
    }

    [Fact]
    public async Task Search_AddMethod_Found()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "add",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("add") && h.Kind == SymbolKind.Method);
    }

    [Fact]
    public async Task GetCard_Calculator_ReturnsCardWithSymbolId()
    {
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Calculator",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 1));

        search.IsSuccess.Should().BeTrue();
        var hit = search.Value.Data.Hits.First();

        var card = await fixture.QueryEngine.GetSymbolCardAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        card.IsSuccess.Should().BeTrue();
        card.Value.Data.Kind.Should().Be(SymbolKind.Class);
        card.Value.Data.SymbolId.Value.Should().StartWith("T:");
    }

    [Fact]
    public async Task Search_PriorityEnum_Found()
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "Priority",
            new SymbolSearchFilters(Kinds: [SymbolKind.Enum]),
            new BudgetLimits(maxResults: 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h =>
            h.FullyQualifiedName.Contains("Priority") && h.Kind == SymbolKind.Enum);
    }
}
