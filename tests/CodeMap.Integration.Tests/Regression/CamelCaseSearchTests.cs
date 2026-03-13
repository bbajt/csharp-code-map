namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Regression tests for FTS CamelCase tokenization (PHASE-07-02 GAP-02 fix).
/// Verifies that CamelCase compound names are findable by component word.
/// New symbols added to SampleSolution: IOrderProcessingService, OrderProcessingService,
/// OrderProcessingResult, CamelCaseStringHelper.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Regression")]
public sealed class CamelCaseSearchTests
{
    private readonly IndexedSampleSolutionFixture _f;

    public CamelCaseSearchTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    [Fact]
    public async Task Regression_CamelCase_SearchByComponentWord_Processing()
    {
        // "Processing" is the 3rd word in "Order-Processing-Service" and "Order-Processing-Result"
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "Processing",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class, SymbolKind.Interface]),
            new BudgetLimits(maxResults: 20));

        result.IsSuccess.Should().BeTrue();
        var names = result.Value.Data.Hits.Select(h => h.FullyQualifiedName).ToList();

        names.Should().Contain(n => n.Contains("OrderProcessingService"),
            "FTS name_tokens for 'OrderProcessingService' includes 'processing'");
    }

    [Fact]
    public async Task Regression_CamelCase_InterfacePrefix_SearchBySecondComponent()
    {
        // "Processing" should also find IOrderProcessingService (I + Order + Processing + Service)
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "Processing",
            new SymbolSearchFilters(Kinds: [SymbolKind.Interface]),
            new BudgetLimits(maxResults: 20));

        result.IsSuccess.Should().BeTrue();
        var names = result.Value.Data.Hits.Select(h => h.FullyQualifiedName).ToList();

        names.Should().Contain(n => n.Contains("IOrderProcessingService"),
            "FTS name_tokens for 'IOrderProcessingService' includes 'processing'");
    }

    [Fact]
    public async Task Regression_CamelCase_SearchByMiddleComponent_StringHelper()
    {
        // "String" is the 3rd word in "Camel-Case-String-Helper"
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "String",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 20));

        result.IsSuccess.Should().BeTrue();
        var names = result.Value.Data.Hits.Select(h => h.FullyQualifiedName).ToList();

        names.Should().Contain(n => n.Contains("CamelCaseStringHelper"),
            "FTS name_tokens for 'CamelCaseStringHelper' includes 'string'");
    }

    [Fact]
    public async Task Regression_CamelCase_ExactSearch_StillWorks()
    {
        // Exact match must still work — regression guard
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "OrderProcessingService",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotBeEmpty(
            "Exact search for 'OrderProcessingService' must still find the class");
    }

    [Fact]
    public async Task Regression_CamelCase_PrefixSearch_StillWorks()
    {
        // Prefix search must still work — regression guard
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "OrderProc*",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotBeEmpty(
            "Prefix search 'OrderProc*' must still find OrderProcessingService");
    }
}
