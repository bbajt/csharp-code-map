namespace CodeMap.Integration.Tests.Workflows;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using FluentAssertions;

/// <summary>
/// Cross-cutting multi-tool workflow tests (PHASE-02-07 T01).
/// Simulates real agent workflows: search → navigate → inspect → repeat.
/// Uses real Roslyn-indexed SampleSolution — no mocks in the query layer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentWorkflowTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public AgentWorkflowTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private CodeMap.Core.Models.RoutingContext Routing => _f.CommittedRouting();

    // ── Workflow 1: "Understand a class" ──────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_UnderstandClass()
    {
        // 1. search for the class
        var search = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "OrderService",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]), null);
        search.IsSuccess.Should().BeTrue();
        search.Value.Data.Hits.Should().NotBeEmpty("OrderService must be indexed");
        var classId = search.Value.Data.Hits.First().SymbolId;

        // 2. get symbol card
        var card = await _f.QueryEngine.GetSymbolCardAsync(Routing, classId);
        card.IsSuccess.Should().BeTrue();
        card.Value.Data.Signature.Should().NotBeNullOrEmpty();

        // 3. find references to the class
        var refs = await _f.QueryEngine.FindReferencesAsync(
            Routing, classId, null, new BudgetLimits(maxResults: 20));
        refs.IsSuccess.Should().BeTrue();

        // 4. get callers of SubmitAsync (one of its methods)
        var callers = await _f.QueryEngine.GetCallersAsync(
            Routing, _f.SubmitAsyncId, depth: 1, limitPerLevel: 20, budgets: null);
        callers.IsSuccess.Should().BeTrue();

        // All 4 calls succeed and are consistent — card belongs to the class we searched for
        card.Value.Data.SymbolId.Should().Be(classId);
    }

    // ── Workflow 2: "Navigate type hierarchy" ─────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_NavigateHierarchy()
    {
        // Order : AuditableEntity : IEntity
        // 1. get hierarchy for Order
        var orderHierarchy = await _f.QueryEngine.GetTypeHierarchyAsync(Routing, _f.OrderId);
        orderHierarchy.IsSuccess.Should().BeTrue();
        orderHierarchy.Value.Data.BaseType.Should().NotBeNull("Order extends AuditableEntity");

        // 2. get card for the base type
        var baseTypeId = orderHierarchy.Value.Data.BaseType!.SymbolId;
        var baseCard = await _f.QueryEngine.GetSymbolCardAsync(Routing, baseTypeId);
        baseCard.IsSuccess.Should().BeTrue();

        // 3. navigate up: get hierarchy of AuditableEntity
        var aeHierarchy = await _f.QueryEngine.GetTypeHierarchyAsync(Routing, baseTypeId);
        aeHierarchy.IsSuccess.Should().BeTrue();
        aeHierarchy.Value.Data.Interfaces.Should().NotBeEmpty("AuditableEntity implements IEntity");

        // 4. get card for the interface
        var ifaceId = aeHierarchy.Value.Data.Interfaces.First().SymbolId;
        var ifaceCard = await _f.QueryEngine.GetSymbolCardAsync(Routing, ifaceId);
        ifaceCard.IsSuccess.Should().BeTrue();
        ifaceCard.Value.Data.Kind.Should().Be(SymbolKind.Interface);
    }

    // ── Workflow 3: "Trace a call chain" ─────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_TraceCallChain()
    {
        // card(SubmitAsync) → callees → card(callee) → refs(callee)
        var card = await _f.QueryEngine.GetSymbolCardAsync(Routing, _f.SubmitAsyncId);
        card.IsSuccess.Should().BeTrue();

        var callees = await _f.QueryEngine.GetCalleesAsync(
            Routing, _f.SubmitAsyncId, depth: 1, limitPerLevel: 20, budgets: null);
        callees.IsSuccess.Should().BeTrue();

        // callees may be empty if SubmitAsync only calls interface/external symbols
        if (callees.Value.Data.Nodes.Count == 0) return;

        // pick first callee and inspect it
        var calleeId = callees.Value.Data.Nodes[0].SymbolId;
        var calleeCard = await _f.QueryEngine.GetSymbolCardAsync(Routing, calleeId);
        calleeCard.IsSuccess.Should().BeTrue();

        // who else calls this callee? (tool succeeds — count may be 0 for external symbols)
        var calleeRefs = await _f.QueryEngine.FindReferencesAsync(
            Routing, calleeId, RefKind.Call, new BudgetLimits(maxResults: 20));
        calleeRefs.IsSuccess.Should().BeTrue();
    }

    // ── Workflow 4: "Find all implementations" ───────────────────────────────

    [Fact]
    public async Task E2E_Workflow_FindImplementations()
    {
        // search(IOrderService) → hierarchy → cards on derived types
        var hierarchy = await _f.QueryEngine.GetTypeHierarchyAsync(Routing, _f.IOrderServiceId);
        hierarchy.IsSuccess.Should().BeTrue();
        hierarchy.Value.Data.DerivedTypes.Should().NotBeEmpty(
            "IOrderService has at least one implementation (OrderService)");

        // verify each derived type has a valid card
        foreach (var derived in hierarchy.Value.Data.DerivedTypes)
        {
            var derivedCard = await _f.QueryEngine.GetSymbolCardAsync(Routing, derived.SymbolId);
            derivedCard.IsSuccess.Should().BeTrue(
                $"derived type {derived.SymbolId} should have a valid card");
        }
    }

    // ── Workflow 5: "Read and inspect code" ──────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_ReadAndInspect()
    {
        // search → definition span → get span (broader)
        var search = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "SubmitAsync",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]), null);
        search.IsSuccess.Should().BeTrue();
        var methodId = search.Value.Data.Hits.First().SymbolId;

        var defSpan = await _f.QueryEngine.GetDefinitionSpanAsync(Routing, methodId, 120, 2);
        defSpan.IsSuccess.Should().BeTrue();

        // broader span: 3 lines before/after the definition
        var fp = defSpan.Value.Data.FilePath;
        var start = Math.Max(1, defSpan.Value.Data.StartLine - 3);
        var end = defSpan.Value.Data.EndLine + 3;
        var wider = await _f.QueryEngine.GetSpanAsync(Routing, fp, start, end, 0, null);
        wider.IsSuccess.Should().BeTrue();

        // the definition content appears inside the wider context
        var defFirstLine = defSpan.Value.Data.Content.Split('\n')[0].Trim();
        wider.Value.Data.Content.Should().Contain(defFirstLine,
            "definition span content must be a subset of broader span");
    }

    // ── Workflow 6: "Cross-reference navigation" ─────────────────────────────

    [Fact]
    public async Task E2E_Workflow_CrossReferenceNavigation()
    {
        // refs(OrderService class) → card on FromSymbol → refs on that symbol
        var refs = await _f.QueryEngine.FindReferencesAsync(
            Routing, _f.OrderServiceId, null, new BudgetLimits(maxResults: 10));
        refs.IsSuccess.Should().BeTrue();

        if (refs.Value.Data.References.Count == 0)
            return; // OrderService not referenced in SampleSolution — skip gracefully

        var fromSymbolId = refs.Value.Data.References[0].FromSymbol;
        var fromCard = await _f.QueryEngine.GetSymbolCardAsync(Routing, fromSymbolId);
        fromCard.IsSuccess.Should().BeTrue();

        // transitive navigation: what does the caller reference?
        var transRefs = await _f.QueryEngine.FindReferencesAsync(
            Routing, fromSymbolId, null, new BudgetLimits(maxResults: 10));
        transRefs.IsSuccess.Should().BeTrue();
    }
}
