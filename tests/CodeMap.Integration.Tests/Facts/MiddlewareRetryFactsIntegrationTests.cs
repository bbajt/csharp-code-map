namespace CodeMap.Integration.Tests.Facts;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Integration tests verifying middleware + retry policy facts flow through
/// the full Roslyn → BaselineStore → QueryEngine pipeline (PHASE-03-06).
/// Uses IndexedSampleSolutionFixture which compiles SampleSolution once per run.
/// SampleApp.Api/MiddlewareSetup.cs + ResilienceSetup.cs provide the test data.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MiddlewareRetryFactsIntegrationTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public MiddlewareRetryFactsIntegrationTests(IndexedSampleSolutionFixture fixture) =>
        _f = fixture;

    // ── Middleware facts ───────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_IndexSampleSolution_MiddlewareFactsExtracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Middleware, limit: 100);

        facts.Should().NotBeEmpty();

        // MiddlewareSetup.cs registers UseHttpsRedirection, UseAuthentication, UseAuthorization
        var methodNames = facts.Select(f => f.Value.Split('|')[0]).ToHashSet();
        methodNames.Should().Contain("UseHttpsRedirection",
            because: "MiddlewareSetup.cs calls app.UseHttpsRedirection()");
        methodNames.Should().Contain("UseAuthentication",
            because: "MiddlewareSetup.cs calls app.UseAuthentication()");
        methodNames.Should().Contain("UseAuthorization",
            because: "MiddlewareSetup.cs calls app.UseAuthorization()");
    }

    [Fact]
    public async Task E2E_IndexSampleSolution_RetryFactsExtracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.RetryPolicy, limit: 100);

        facts.Should().NotBeEmpty(
            because: "ResilienceSetup.cs calls RetryAsync(3) and WaitAndRetryAsync(3)");

        var methodNames = facts.Select(f => f.Value.Split('|')[0]).ToHashSet();
        methodNames.Should().Contain(s => s.StartsWith("RetryAsync"),
            because: "ResilienceSetup.Configure() calls RetryAsync(3)");
    }

    [Fact]
    public async Task E2E_MiddlewareFacts_PipelineOrderCorrect()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Middleware, limit: 100);

        // Find the facts from MiddlewareSetup.Configure (same SymbolId)
        // Group by containing symbol and look for the Configure method
        var configureFacts = facts
            .Where(f => f.Value.Contains("UseHttpsRedirection")
                     || f.Value.Contains("UseAuthentication")
                     || f.Value.Contains("UseAuthorization"))
            .ToList();

        configureFacts.Should().HaveCountGreaterThanOrEqualTo(3,
            because: "UseHttpsRedirection, UseAuthentication, UseAuthorization must all be present");

        // Parse positions and verify ordering
        static int ParsePosition(StoredFact f)
        {
            // Value format: "MethodName|pos:N" or "MethodName|pos:N|terminal"
            var posSegment = f.Value.Split('|').FirstOrDefault(s => s.StartsWith("pos:"));
            return posSegment is not null && int.TryParse(posSegment["pos:".Length..], out var n)
                ? n : int.MaxValue;
        }

        var httpsPos = ParsePosition(configureFacts.First(f => f.Value.Contains("UseHttpsRedirection")));
        var authPos = ParsePosition(configureFacts.First(f => f.Value.Contains("UseAuthentication")));
        var authzPos = ParsePosition(configureFacts.First(f => f.Value.Contains("UseAuthorization")));

        httpsPos.Should().BeLessThan(authPos,
            because: "UseHttpsRedirection comes before UseAuthentication in the pipeline");
        authPos.Should().BeLessThan(authzPos,
            because: "UseAuthentication comes before UseAuthorization in the pipeline");
    }

    // ── Symbol card hydration ─────────────────────────────────────────────────

    [Fact]
    public async Task E2E_SymbolCard_MiddlewareFact_Visible()
    {
        // Get middleware facts and use their SymbolId to fetch the card
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Middleware, limit: 100);

        if (facts.Count == 0) return; // no middleware facts — skip

        // Use the first fact's SymbolId (it points to the Configure method)
        var firstFact = facts[0];
        if (firstFact.SymbolId == Core.Types.SymbolId.Empty) return;

        var routing = _f.CommittedRouting();
        var card = await _f.QueryEngine.GetSymbolCardAsync(routing, firstFact.SymbolId);

        card.IsSuccess.Should().BeTrue();
        card.Value.Data.Facts.Should().Contain(
            f => f.Kind == FactKind.Middleware,
            because: "the method containing Use*/Map* calls should have Middleware facts on its card");
    }

    [Fact]
    public async Task E2E_SymbolCard_RetryFact_Visible()
    {
        // Get retry facts and use their SymbolId to fetch the card
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.RetryPolicy, limit: 100);

        if (facts.Count == 0) return; // no retry facts — skip

        var firstFact = facts[0];
        if (firstFact.SymbolId == Core.Types.SymbolId.Empty) return;

        var routing = _f.CommittedRouting();
        var card = await _f.QueryEngine.GetSymbolCardAsync(routing, firstFact.SymbolId);

        card.IsSuccess.Should().BeTrue();
        card.Value.Data.Facts.Should().Contain(
            f => f.Kind == FactKind.RetryPolicy,
            because: "the method containing retry calls should have RetryPolicy facts on its card");
    }
}
