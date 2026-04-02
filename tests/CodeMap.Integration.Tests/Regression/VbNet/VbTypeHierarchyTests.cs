namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// VB.NET regression: type hierarchy (base class, implemented interfaces).
/// Order implements IEntity; OrderService implements IOrderService;
/// AppDbContext inherits DbContext.
/// </summary>
[Trait("Category", "Integration")]
[Collection("VbRegression")]
public sealed class VbTypeHierarchyTests(IndexedSampleVbSolutionFixture fixture)
{
    [Fact]
    public async Task Order_ImplementsIEntity()
    {
        var hit = await FindSymbolAsync("Order", SymbolKind.Class);

        var hierarchy = await fixture.QueryEngine.GetTypeHierarchyAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.Interfaces.Should().Contain(t =>
            t.DisplayName.Contains("IEntity"),
            "Order has `Implements IEntity` in Order.vb");
    }

    [Fact]
    public async Task OrderService_ImplementsIOrderService()
    {
        var hit = await FindSymbolAsync("OrderService", SymbolKind.Class);

        var hierarchy = await fixture.QueryEngine.GetTypeHierarchyAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.Interfaces.Should().Contain(t =>
            t.DisplayName.Contains("IOrderService"),
            "OrderService has `Implements IOrderService` in OrderService.vb");
    }

    [Fact]
    public async Task AppDbContext_InheritsDbContext()
    {
        var hit = await FindSymbolAsync("AppDbContext", SymbolKind.Class);

        var hierarchy = await fixture.QueryEngine.GetTypeHierarchyAsync(
            fixture.CommittedRouting(), hit.SymbolId);

        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.BaseType?.DisplayName.Should().Contain("DbContext",
            "AppDbContext has `Inherits DbContext` in AppDbContext.vb");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<SymbolSearchHit> FindSymbolAsync(string name, SymbolKind kind)
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), name,
            new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 5));
        result.IsSuccess.Should().BeTrue();
        return result.Value.Data.Hits.First(h => h.FullyQualifiedName.Contains(name));
    }
}
