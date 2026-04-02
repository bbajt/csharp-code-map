namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// VB.NET regression: callers, callees, refs.find, and list_endpoints.
/// All graph/ref queries should succeed without error for VB.NET symbols.
/// </summary>
[Trait("Category", "Integration")]
[Collection("VbRegression")]
public sealed class VbReferencesTests(IndexedSampleVbSolutionFixture fixture)
{
    [Fact]
    public async Task FindCallers_GetOrderAsync_OperationSucceeds()
    {
        var hit = await FindMethodAsync("GetOrderAsync");

        var callers = await fixture.QueryEngine.GetCallersAsync(
            fixture.CommittedRouting(), hit.SymbolId,
            depth: 3, limitPerLevel: 20, budgets: null);

        callers.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task FindCallees_SubmitOrderAsync_OperationSucceeds()
    {
        var hit = await FindMethodAsync("SubmitOrderAsync");

        var callees = await fixture.QueryEngine.GetCalleesAsync(
            fixture.CommittedRouting(), hit.SymbolId,
            depth: 3, limitPerLevel: 20, budgets: null);

        callees.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task FindRefs_IOrderService_OperationSucceeds()
    {
        var hit = await FindSymbolAsync("IOrderService", SymbolKind.Interface);

        var refs = await fixture.QueryEngine.FindReferencesAsync(
            fixture.CommittedRouting(), hit.SymbolId,
            kind: null, budgets: null);

        refs.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ListEndpoints_VbSolution_ReturnsOrdersControllerRoutes()
    {
        var result = await fixture.QueryEngine.ListEndpointsAsync(
            fixture.CommittedRouting(), pathFilter: null, httpMethod: null, limit: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Endpoints.Should().NotBeEmpty(
            "OrdersController.vb has [HttpGet], [HttpPost], [HttpDelete]");
        result.Value.Data.Endpoints.Should().Contain(e =>
            e.HttpMethod != null &&
            e.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<SymbolSearchHit> FindMethodAsync(string name)
    {
        var result = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), name,
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));
        result.IsSuccess.Should().BeTrue();
        return result.Value.Data.Hits.First(h => h.FullyQualifiedName.Contains(name));
    }

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
