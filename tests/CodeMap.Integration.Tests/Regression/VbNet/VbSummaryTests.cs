namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// VB.NET regression: codemap.summarize, codemap.export, and surfaces tools.
/// Verifies that the whole pipeline (extraction → storage → query) produces
/// a meaningful summary for a pure VB.NET solution.
/// </summary>
[Trait("Category", "Integration")]
[Collection("VbRegression")]
public sealed class VbSummaryTests(IndexedSampleVbSolutionFixture fixture)
{
    [Fact]
    public async Task Summarize_VbSolution_ReturnsMeaningfulSummary()
    {
        var result = await fixture.QueryEngine.SummarizeAsync(
            fixture.CommittedRouting());

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Stats.SymbolCount.Should().BeGreaterThan(10,
            "SampleVbSolution has 17 .vb files with many types and methods");
        result.Value.Data.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SemanticLevel_IsFull_ForVbSolution()
    {
        var result = await fixture.QueryEngine.SummarizeAsync(
            fixture.CommittedRouting());

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Stats.SemanticLevel.Should().Be(SemanticLevel.Full,
            "SampleVbSolution compiles cleanly — all symbols resolved semantically");
    }

    [Fact]
    public async Task Export_VbSolution_IncludesVbSymbols()
    {
        var result = await fixture.QueryEngine.ExportAsync(
            fixture.CommittedRouting(),
            detail: "standard",
            maxTokens: 50_000);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListDbTables_VbSolution_ReturnsOrdersAndCustomers()
    {
        var result = await fixture.QueryEngine.ListDbTablesAsync(
            fixture.CommittedRouting(), tableFilter: null, limit: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Tables.Should().Contain(t => t.TableName.Contains("Order"),
            "AppDbContext.vb has DbSet(Of Order) → table name 'Orders'");
        result.Value.Data.Tables.Should().Contain(t => t.TableName.Contains("Customer"),
            "AppDbContext.vb has DbSet(Of Customer) → table name 'Customers'");
    }

    [Fact]
    public async Task ListConfigKeys_VbSolution_ReturnsKnownKeys()
    {
        var result = await fixture.QueryEngine.ListConfigKeysAsync(
            fixture.CommittedRouting(), keyFilter: null, limit: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Keys.Should().Contain(k => k.Key.Contains("App:MaxRetries"),
            "ConfigConsumer.vb calls GetValue<Integer>(\"App:MaxRetries\")");
    }
}
