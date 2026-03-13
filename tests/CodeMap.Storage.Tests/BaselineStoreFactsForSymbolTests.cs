namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

public class BaselineStoreFactsForSymbolTests : IDisposable
{
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));

    private readonly string _tempDir;
    private readonly BaselineStore _store;

    public BaselineStoreFactsForSymbolTests()
        => (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly FilePath File1 = FilePath.From("src/Startup.cs");
    private static readonly SymbolId Sym1 = SymbolId.From("M:App.Startup.Configure");
    private static readonly SymbolId Sym2 = SymbolId.From("M:App.Startup.OtherMethod");

    private ExtractedFact MakeFact(SymbolId symbolId, string value, FactKind kind = FactKind.DiRegistration) =>
        new(
            SymbolId: symbolId,
            StableId: null,
            Kind: kind,
            Value: value,
            FilePath: File1,
            LineStart: 5,
            LineEnd: 5,
            Confidence: Confidence.High);

    private async Task SeedAsync(SymbolId symbolId, IReadOnlyList<ExtractedFact>? facts = null)
    {
        var file = StorageTestHelpers.MakeFile(File1.Value, "file001");
        var symbol = StorageTestHelpers.MakeSymbol(
            symbolId.Value, symbolId.Value, SymbolKind.Method, File1.Value);
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
    public async Task GetFactsForSymbol_HasFacts_ReturnsFacts()
    {
        await SeedAsync(Sym1, [MakeFact(Sym1, "IService \u2192 ServiceImpl|Scoped")]);

        var facts = await _store.GetFactsForSymbolAsync(Repo, Sha, Sym1);

        facts.Should().HaveCount(1);
        facts[0].SymbolId.Should().Be(Sym1);
        facts[0].Kind.Should().Be(FactKind.DiRegistration);
        facts[0].Value.Should().Contain("IService");
    }

    [Fact]
    public async Task GetFactsForSymbol_NoFacts_ReturnsEmpty()
    {
        await SeedAsync(Sym1, []);

        var facts = await _store.GetFactsForSymbolAsync(Repo, Sha, Sym1);

        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFactsForSymbol_MultipleFacts_ReturnsAll()
    {
        await SeedAsync(Sym1, [
            MakeFact(Sym1, "IServiceA \u2192 ServiceA|Scoped"),
            MakeFact(Sym1, "IServiceB \u2192 ServiceB|Singleton"),
        ]);

        var facts = await _store.GetFactsForSymbolAsync(Repo, Sha, Sym1);

        facts.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFactsForSymbol_DifferentSymbol_NotReturned()
    {
        var file2 = StorageTestHelpers.MakeFile(File1.Value, "file001");
        var sym1obj = StorageTestHelpers.MakeSymbol(Sym1.Value, Sym1.Value, SymbolKind.Method, File1.Value);
        var sym2obj = StorageTestHelpers.MakeSymbol(Sym2.Value, Sym2.Value, SymbolKind.Method, File1.Value);
        var result = new CompilationResult(
            Symbols: [sym1obj, sym2obj],
            References: [],
            Files: [file2],
            Stats: new IndexStats(2, 0, 1, 0.01, Confidence.High),
            Facts: [
                MakeFact(Sym1, "IServiceA \u2192 ServiceA|Scoped"),
                MakeFact(Sym2, "IServiceB \u2192 ServiceB|Singleton"),
            ]);
        await _store.CreateBaselineAsync(Repo, Sha, result, _tempDir);

        var factsForSym1 = await _store.GetFactsForSymbolAsync(Repo, Sha, Sym1);

        factsForSym1.Should().HaveCount(1);
        factsForSym1[0].SymbolId.Should().Be(Sym1);
    }
}
