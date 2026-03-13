namespace CodeMap.Integration.Tests.StableId;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Integration.Tests.Workflows;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// End-to-end integration tests for SSID (Stable Symbol Identity, PHASE-03-01).
/// AC-T05-01: All symbols in SampleSolution have unique stable_ids.
/// AC-T05-04: Old baselines without stable_id still work (backward compat).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StableIdIntegrationTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public StableIdIntegrationTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    // ── AC-T05-01: All symbols have non-null stable_id ─────────────────────────

    [Fact]
    public async Task E2E_IndexSampleSolution_AllSymbolsHaveStableId()
    {
        var routing = _f.CommittedRouting();

        // Collect a large sample of symbols via search
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            routing, "a", null, new BudgetLimits(maxResults: 100));
        result.IsSuccess.Should().BeTrue();

        // Verify every returned symbol has a stable_id
        foreach (var hit in result.Value.Data.Hits)
        {
            var card = await _f.QueryEngine.GetSymbolCardAsync(routing, hit.SymbolId);
            card.IsSuccess.Should().BeTrue();
            card.Value.Data.StableId.Should().NotBeNull(
                because: $"symbol {hit.SymbolId.Value} should have a stable_id after PHASE-03-01 indexing");
            card.Value.Data.StableId!.Value.Value.Should().StartWith("sym_",
                because: "stable_ids must have the sym_ prefix");
        }
    }

    // ── AC-T05-01: stable_ids are unique across the index ────────────────────

    [Fact]
    public async Task E2E_StableId_UniqueAcrossEntireIndex()
    {
        var routing = _f.CommittedRouting();

        // Gather a broad sample
        var result = await _f.QueryEngine.SearchSymbolsAsync(
            routing, "a", null, new BudgetLimits(maxResults: 100));
        result.IsSuccess.Should().BeTrue();

        var stableIds = new HashSet<string>();
        foreach (var hit in result.Value.Data.Hits)
        {
            var card = await _f.QueryEngine.GetSymbolCardAsync(routing, hit.SymbolId);
            if (!card.IsSuccess || card.Value.Data.StableId is null) continue;
            var sid = card.Value.Data.StableId.Value.Value;
            stableIds.Add(sid).Should().BeTrue(
                because: $"stable_id {sid} for symbol {hit.SymbolId.Value} must be unique");
        }
    }

    // ── GetCardByStableId works end-to-end ────────────────────────────────────

    [Fact]
    public async Task E2E_GetCardByStableId_ReturnsCorrectSymbol()
    {
        var routing = _f.CommittedRouting();

        // 1. Get OrderService card to retrieve its stable_id
        var cardResult = await _f.QueryEngine.GetSymbolCardAsync(routing, _f.OrderServiceId);
        cardResult.IsSuccess.Should().BeTrue();
        cardResult.Value.Data.StableId.Should().NotBeNull(
            because: "OrderService must have a stable_id after indexing");

        var stable = cardResult.Value.Data.StableId!.Value;

        // 2. Look up by stable_id
        var byStable = await _f.QueryEngine.GetSymbolByStableIdAsync(routing, stable);
        byStable.IsSuccess.Should().BeTrue();
        byStable.Value.Data.SymbolId.Should().Be(_f.OrderServiceId,
            because: "GetSymbolByStableIdAsync must resolve to the original symbol");
    }

    // ── AC-T05-04: Old baseline (no stable_ids) is backward-compatible ─────────

    [Fact]
    public async Task E2E_OldBaselineWithoutStableId_GetCardStillWorks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codemap-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var repoId = RepoId.From("compat-repo");
            var sha = CommitSha.From(new string('f', 40));
            var symId = SymbolId.From("T:OldClass");
            var filePath = FilePath.From("src/OldClass.cs");

            var baselineDir = Path.Combine(tempDir, "baselines");
            Directory.CreateDirectory(baselineDir);

            var factory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
            var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);

            // Seed a symbol WITHOUT a stable_id (simulating old baseline)
            var card = SymbolCard.CreateMinimal(
                symbolId: symId,
                fullyQualifiedName: "OldClass",
                kind: SymbolKind.Class,
                signature: "class OldClass",
                @namespace: "Legacy",
                filePath: filePath,
                spanStart: 1,
                spanEnd: 10,
                visibility: "public",
                confidence: Confidence.High);
            // No StableId set — simulates pre-PHASE-03-01 baseline

            var fileEntry = new ExtractedFile("file001", filePath, new string('a', 64), null);
            var compilation = new CompilationResult([card], [], [fileEntry],
                new IndexStats(1, 0, 1, 0.01, Confidence.High));
            await store.CreateBaselineAsync(repoId, sha, compilation, tempDir);

            // Create query engine and verify GetSymbolCardAsync still works
            var cache = new InMemoryCacheService();
            var tracker = new TokenSavingsTracker();
            var engine = new QueryEngine(store, cache, tracker,
                new ExcerptReader(store), new GraphTraverser(), new FeatureTracer(store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

            var routing = new RoutingContext(repoId, baselineCommitSha: sha);
            var result = await engine.GetSymbolCardAsync(routing, symId);

            result.IsSuccess.Should().BeTrue("old baseline queries must succeed");
            result.Value.Data.FullyQualifiedName.Should().Be("OldClass");
            result.Value.Data.StableId.Should().BeNull(
                because: "symbols without stable_ids return null, not an error");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── GetSymbolByStableId returns NOT_FOUND for unknown stable_id ───────────

    [Fact]
    public async Task E2E_GetCardByStableId_UnknownStableId_ReturnsNotFound()
    {
        var routing = _f.CommittedRouting();
        var unknown = new StableId("sym_" + new string('0', 16));

        var result = await _f.QueryEngine.GetSymbolByStableIdAsync(routing, unknown);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }
}
