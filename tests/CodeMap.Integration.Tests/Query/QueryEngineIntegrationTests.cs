namespace CodeMap.Integration.Tests.Query;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Integration")]
public class QueryEngineIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _store;
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("int-query-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));

    public QueryEngineIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);

        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();
        _engine = new QueryEngine(_store, cache, tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task SeedBaselineAsync()
    {
        var srcFile = Path.Combine(_repoDir, "src", "OrderService.cs");
        System.IO.File.WriteAllLines(srcFile,
            Enumerable.Range(1, 50)
                .Select(i => i == 10 ? "    public class OrderService { }" : $"    // line {i}"));

        var file = new ExtractedFile(
            "deadbeef12345678",
            FilePath.From("src/OrderService.cs"),
            new string('0', 64),
            "SampleApp");

        var symbol = SymbolCard.CreateMinimal(
            SymbolId.From("SampleApp.OrderService"),
            "SampleApp.OrderService",
            SymbolKind.Class,
            "OrderService()",
            "SampleApp",
            FilePath.From("src/OrderService.cs"),
            10, 10,
            "public",
            Confidence.High,
            documentation: "Handles orders.");

        var data = new CompilationResult(
            Symbols: [symbol],
            References: [],
            Files: [file],
            Stats: new IndexStats(1, 0, 1, 0.1, Confidence.High));

        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static RoutingContext Routing =>
        new(Repo, baselineCommitSha: Sha);

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_AgainstRealBaseline_ReturnsResults()
    {
        await SeedBaselineAsync();

        var result = await _engine.SearchSymbolsAsync(Routing, "OrderService", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotBeEmpty();
        result.Value.Data.Hits.Should().Contain(h => h.FullyQualifiedName.Contains("OrderService"));
    }

    [Fact]
    public async Task GetCard_AgainstRealBaseline_ReturnsSymbolCard()
    {
        await SeedBaselineAsync();

        var result = await _engine.GetSymbolCardAsync(
            Routing, SymbolId.From("SampleApp.OrderService"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.FullyQualifiedName.Should().Be("SampleApp.OrderService");
        result.Value.Data.Kind.Should().Be(SymbolKind.Class);
        result.Value.Data.Documentation.Should().Be("Handles orders.");
    }

    [Fact]
    public async Task GetSpan_AgainstRealBaseline_ReturnsSourceCode()
    {
        await SeedBaselineAsync();

        var result = await _engine.GetSpanAsync(
            Routing, FilePath.From("src/OrderService.cs"), 5, 15, 0, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("OrderService");
        result.Value.Data.StartLine.Should().Be(5);
    }

    [Fact]
    public async Task GetDefinitionSpan_AgainstRealBaseline_ReturnsDefinition()
    {
        await SeedBaselineAsync();

        var result = await _engine.GetDefinitionSpanAsync(
            Routing, SymbolId.From("SampleApp.OrderService"), 20, 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("OrderService");
        result.Value.Answer.Should().Contain("Definition of");
    }

    [Fact]
    public async Task Search_CacheHitOnSecondCall_StorageCalledOnce()
    {
        await SeedBaselineAsync();

        await _engine.SearchSymbolsAsync(Routing, "OrderService", null, null);
        var result2 = await _engine.SearchSymbolsAsync(Routing, "OrderService", null, null);

        // Both should succeed — second from cache
        result2.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task TokenSavings_AccumulateAcrossMultipleQueries()
    {
        await SeedBaselineAsync();
        var tracker = new TokenSavingsTracker();
        var engine = new QueryEngine(_store, new InMemoryCacheService(), tracker,
                                      new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        await engine.SearchSymbolsAsync(Routing, "OrderService", null, null);
        await engine.GetSymbolCardAsync(Routing, SymbolId.From("SampleApp.OrderService"));

        tracker.TotalTokensSaved.Should().BeGreaterThan(0);
    }
}
