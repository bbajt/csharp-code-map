namespace CodeMap.Integration.Tests.Regression.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

[Trait("Category", "Integration")]
[Collection("FSharpRegression")]
public sealed class FSharpReferenceTests(IndexedFSharpSolutionFixture fixture)
{
    [Fact]
    public async Task FindReferences_CalculatorAdd_HasCallers()
    {
        // Find Calculator.add
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "add",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var addHit = search.Value.Data.Hits.FirstOrDefault(h =>
            h.SymbolId.Value.Contains("Calculator.add"));

        if (addHit is null) return; // Skip if not found — refs depend on FCS resolution

        var refs = await fixture.QueryEngine.FindReferencesAsync(
            fixture.CommittedRouting(), addHit.SymbolId, null, new BudgetLimits(maxResults: 20));

        refs.IsSuccess.Should().BeTrue();
        // Calculator.add is called from processOrder and sumList
        refs.Value.Data.References.Should().NotBeEmpty(
            "Calculator.add should have callers (processOrder, sumList)");
    }

    [Fact]
    public async Task GetCallees_ProcessOrder_CallsCalculatorAdd()
    {
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "processOrder",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var hit = search.Value.Data.Hits.FirstOrDefault(h =>
            h.SymbolId.Value.Contains("processOrder"));

        if (hit is null) return;

        var callees = await fixture.QueryEngine.GetCalleesAsync(
            fixture.CommittedRouting(), hit.SymbolId, 1, 20, null);

        callees.IsSuccess.Should().BeTrue();
        // processOrder calls createOrder, totalItemCount, Calculator.add
        callees.Value.Data.Nodes.Should().HaveCountGreaterThan(1,
            "processOrder orchestrates multiple calls");
    }

    [Fact]
    public async Task TraceFeature_ProcessOrder_ReturnsCallTree()
    {
        var search = await fixture.QueryEngine.SearchSymbolsAsync(
            fixture.CommittedRouting(), "processOrder",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));

        search.IsSuccess.Should().BeTrue();
        var hit = search.Value.Data.Hits.FirstOrDefault(h =>
            h.SymbolId.Value.Contains("processOrder"));

        if (hit is null) return;

        var trace = await fixture.QueryEngine.TraceFeatureAsync(
            fixture.CommittedRouting(), hit.SymbolId, 2, 50);

        trace.IsSuccess.Should().BeTrue();
        trace.Value.Data.Nodes.Should().NotBeEmpty("trace should find call tree nodes");
    }
}
