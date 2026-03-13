namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

public class BaselineStoreFactsTests : IDisposable
{
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));

    private readonly string _tempDir;
    private readonly BaselineStore _store;

    public BaselineStoreFactsTests()
        => (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly FilePath File1 = FilePath.From("src/Api.cs");
    private static readonly SymbolId Sym1 = SymbolId.From("M:App.Controller.GetAll");
    private static readonly StableId Stable = new("sym_" + new string('e', 16));

    private ExtractedFact MakeRoute(
        string value = "GET /api/orders",
        FactKind kind = FactKind.Route,
        StableId? stableId = null) =>
        new(
            SymbolId: Sym1,
            StableId: stableId ?? Stable,
            Kind: kind,
            Value: value,
            FilePath: File1,
            LineStart: 10,
            LineEnd: 15,
            Confidence: Confidence.High);

    private async Task SeedAsync(IReadOnlyList<ExtractedFact>? facts = null)
    {
        var file = StorageTestHelpers.MakeFile("src/Api.cs", "file001");
        var symbol = StorageTestHelpers.MakeSymbol(
            Sym1.Value, "Controller.GetAll", SymbolKind.Method, File1.Value);
        var result = new CompilationResult(
            Symbols: [symbol],
            References: [],
            Files: [file],
            Stats: new IndexStats(1, 0, 1, 0.01, Confidence.High),
            Facts: facts ?? []);
        await _store.CreateBaselineAsync(Repo, Sha, result, _tempDir);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertFacts_ThenQuery_ReturnsFacts()
    {
        await SeedAsync([MakeRoute()]);

        var facts = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 10);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("GET /api/orders");
        facts[0].SymbolId.Should().Be(Sym1);
        facts[0].Kind.Should().Be(FactKind.Route);
        facts[0].FilePath.Should().Be(File1);
    }

    [Fact]
    public async Task InsertFacts_QueryByKind_ReturnsOnlyMatchingKind()
    {
        var route = MakeRoute("GET /api/orders", FactKind.Route);
        var config = MakeRoute("ConnectionStrings:Default", FactKind.Config);
        await SeedAsync([route, config]);

        var routes = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 10);
        var configs = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Config, 10);

        routes.Should().HaveCount(1);
        routes[0].Kind.Should().Be(FactKind.Route);
        configs.Should().HaveCount(1);
        configs[0].Kind.Should().Be(FactKind.Config);
    }

    [Fact]
    public async Task InsertFacts_WithStableId_StableIdStored()
    {
        await SeedAsync([MakeRoute(stableId: Stable)]);

        var facts = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 10);

        facts.Should().HaveCount(1);
        facts[0].StableId.Should().NotBeNull();
        facts[0].StableId!.Value.Should().Be(Stable);
    }

    [Fact]
    public async Task InsertFacts_QueryRespectLimit()
    {
        await SeedAsync([
            MakeRoute("GET /api/a"),
            MakeRoute("GET /api/b"),
            MakeRoute("GET /api/c"),
        ]);

        var facts = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 2);

        facts.Should().HaveCount(2);
    }

    [Fact]
    public async Task InsertFacts_EmptyList_NoError()
    {
        await SeedAsync([]);  // no exception

        var facts = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 10);
        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFactsByKind_NoFacts_ReturnsEmpty()
    {
        await SeedAsync(null);  // no facts in result

        var facts = await _store.GetFactsByKindAsync(Repo, Sha, FactKind.Route, 10);
        facts.Should().BeEmpty();
    }
}
