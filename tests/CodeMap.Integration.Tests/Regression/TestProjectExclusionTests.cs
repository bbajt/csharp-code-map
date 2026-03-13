namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Regression tests verifying test project fact exclusion (PHASE-07-02 fix).
/// Assert: SampleApp.Tests symbols ARE indexed, but NO facts are extracted from it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Regression")]
public sealed class TestProjectExclusionTests
{
    private readonly IndexedSampleSolutionFixture _f;

    public TestProjectExclusionTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    [Fact]
    public async Task Regression_TestProject_Symbols_AreIndexed()
    {
        // Symbols from SampleApp.Tests MUST still be indexed (only facts are excluded)
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "OrderServiceTests",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 5));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotBeEmpty(
            "OrderServiceTests class from SampleApp.Tests must be indexed");
    }

    [Fact]
    public async Task Regression_TestProject_NoExceptionFacts_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Exception, limit: 500);

        // No fact should have a file path from the test project
        facts.Should().NotContain(
            f => f.FilePath.Value.Replace('\\', '/').Contains("SampleApp.Tests/"),
            "PHASE-07-02 fix: test projects (.Tests suffix) must be excluded from fact extraction");
    }

    [Fact]
    public async Task Regression_TestProject_NoLogFacts_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Log, limit: 500);

        facts.Should().NotContain(
            f => f.FilePath.Value.Replace('\\', '/').Contains("SampleApp.Tests/"),
            "No log facts should originate from the SampleApp.Tests project");
    }

    [Fact]
    public async Task Regression_TestProject_NoDiFacts_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 500);

        facts.Should().NotContain(
            f => f.FilePath.Value.Replace('\\', '/').Contains("SampleApp.Tests/"),
            "No DI registration facts should originate from the SampleApp.Tests project");
    }
}
