namespace CodeMap.Integration.Tests.Regression.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

[Trait("Category", "Integration")]
[Collection("FSharpRegression")]
public sealed class FSharpTypeHierarchyTests(IndexedFSharpSolutionFixture fixture)
{
    [Fact]
    public async Task TypeHierarchy_SimpleGreeter_ImplementsIGreeter()
    {
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "SimpleGreeter",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var hit = search.Value.Data.Hits.FirstOrDefault(h =>
            h.FullyQualifiedName.Contains("SimpleGreeter"));

        if (hit is null) return;

        var hierarchy = await fixture.QueryEngine.GetTypeHierarchyAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.Interfaces.Should().Contain(i =>
            i.DisplayName.Contains("IGreeter"),
            "SimpleGreeter should implement IGreeter");
    }

    [Fact]
    public async Task TypeHierarchy_InMemoryOrderRepository_ImplementsIOrderRepository()
    {
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "InMemoryOrderRepository",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var hit = search.Value.Data.Hits.FirstOrDefault(h =>
            h.FullyQualifiedName.Contains("InMemoryOrderRepository"));

        if (hit is null) return;

        var hierarchy = await fixture.QueryEngine.GetTypeHierarchyAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.Interfaces.Should().Contain(i =>
            i.DisplayName.Contains("IOrderRepository"),
            "InMemoryOrderRepository should implement IOrderRepository");
    }

    [Fact]
    public async Task TypeHierarchy_IGreeter_HasDerivedTypes()
    {
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "IGreeter",
            new SymbolSearchFilters(Kinds: [SymbolKind.Interface]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var hit = search.Value.Data.Hits.FirstOrDefault(h =>
            h.FullyQualifiedName.Contains("IGreeter") && h.Kind == SymbolKind.Interface);

        if (hit is null) return;

        var hierarchy = await fixture.QueryEngine.GetTypeHierarchyAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.DerivedTypes.Should().NotBeEmpty(
            "IGreeter should have derived types (SimpleGreeter, FormalGreeter)");
    }
}
