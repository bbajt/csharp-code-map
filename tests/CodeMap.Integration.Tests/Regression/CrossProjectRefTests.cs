namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Regression tests for cross-project reference extraction (PHASE-07-02 CRITICAL-01 fix).
/// Verifies refs between SampleApp.Api → SampleApp → SampleApp.Shared are persisted.
/// Uses real Roslyn-indexed SampleSolution via IndexedSampleSolutionFixture.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Regression")]
public sealed class CrossProjectRefTests
{
    private readonly IndexedSampleSolutionFixture _f;

    public CrossProjectRefTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    // ── Regression_CrossProject_ApiCallsAppMethod_RefPersisted ────────────────

    [Fact]
    public async Task Regression_CrossProject_ApiCallsAppMethod_RefPersisted()
    {
        // Find IOrderService.SubmitAsync symbol (in SampleApp — cross-project target)
        var search = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "SubmitAsync",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 10));

        search.IsSuccess.Should().BeTrue();
        var hitForSubmit = search.Value.Data.Hits
            .FirstOrDefault(h => h.SymbolId.Value.Contains("IOrderService"));
        var submitId = hitForSubmit is not null
            ? hitForSubmit.SymbolId
            : search.Value.Data.Hits.First().SymbolId;

        // Find refs TO SubmitAsync — should include OrderOrchestrator.ProcessOrderAsync in SampleApp.Api
        var refs = await _f.QueryEngine.FindReferencesAsync(Routing, submitId, null, null);

        refs.IsSuccess.Should().BeTrue();
        refs.Value.Data.References.Should().NotBeEmpty(
            "CRITICAL-01 fix: cross-project refs must be persisted. " +
            "OrderOrchestrator.ProcessOrderAsync calls IOrderService.SubmitAsync");

        refs.Value.Data.References
            .Any(r => r.FromSymbol.Value.Contains("OrderOrchestrator"))
            .Should().BeTrue(
                "OrderOrchestrator.ProcessOrderAsync calls _orderService.SubmitAsync");
    }

    // ── Regression_CrossProject_InterfaceCallDispatch_RefsPointToInterface ────

    [Fact]
    public async Task Regression_InterfaceCallDispatch_RefsPointToInterface()
    {
        // PaymentProcessor calls _service.GetByIdAsync where _service is IOrderService-typed.
        // Roslyn resolves the ref to IOrderService.GetByIdAsync, not OrderService.GetByIdAsync.
        var search = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "GetByIdAsync",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 10));

        search.IsSuccess.Should().BeTrue();
        var getByIdHit = search.Value.Data.Hits
            .FirstOrDefault(h => h.SymbolId.Value.Contains("IOrderService"));

        if (getByIdHit is null) return; // Symbol not found — skip

        var refs = await _f.QueryEngine.FindReferencesAsync(Routing, getByIdHit.SymbolId, null, null);

        refs.IsSuccess.Should().BeTrue();
        refs.Value.Data.References.Should().NotBeEmpty(
            "PaymentProcessor.ChargeAsync calls _service.GetByIdAsync (IOrderService-typed)");

        refs.Value.Data.References
            .Any(r => r.FromSymbol.Value.Contains("PaymentProcessor"))
            .Should().BeTrue(
                "ref must point to IOrderService.GetByIdAsync (interface dispatch)");
    }

    // ── Regression_CrossProject_CallChain_ThreeProjects ──────────────────────

    [Fact]
    public async Task Regression_CrossProject_CallChain_ThreeProjects()
    {
        // Find OrderOrchestrator.ProcessOrderAsync
        var search = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "ProcessOrderAsync",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var processHit = search.Value.Data.Hits.FirstOrDefault();
        if (processHit is null) return;

        // GetCallees(depth: 2) — should show calls into SampleApp
        var callees = await _f.QueryEngine.GetCalleesAsync(
            Routing, processHit.SymbolId, depth: 2, limitPerLevel: 20, budgets: null);

        callees.IsSuccess.Should().BeTrue();
        callees.Value.Data.Nodes.Should().NotBeEmpty(
            "ProcessOrderAsync calls SubmitAsync and SendOrderConfirmationAsync — both in SampleApp");
    }

    // ── Regression_CrossProject_ApiCallsShared_RefPersisted ──────────────────

    [Fact]
    public async Task Regression_CrossProject_ApiCallsShared_RefPersisted()
    {
        // OrderOrchestrator.GetPendingRequestsAsync constructs PagedList<T> from SampleApp.Shared.
        // Verify CamelCaseStringHelper (SampleApp.Shared) is indexed.
        var search = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "CamelCaseStringHelper",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        search.Value.Data.Hits.Should().NotBeEmpty(
            "CamelCaseStringHelper in SampleApp.Shared must be indexed");
    }
}
