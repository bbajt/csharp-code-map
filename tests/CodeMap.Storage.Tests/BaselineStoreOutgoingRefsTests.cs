namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;

public class BaselineStoreOutgoingRefsTests : IDisposable
{
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = StorageTestHelpers.TestSha;

    // Symbols: Caller → Callee → Deep (chain A→B→C)
    private static readonly ExtractedFile FileA = StorageTestHelpers.MakeFile("src/ServiceA.cs", "aaaa000011111111");
    private static readonly ExtractedFile FileB = StorageTestHelpers.MakeFile("src/ServiceB.cs", "bbbb000022222222");

    private static readonly string CallerSym = "M:MyNs.ServiceA.DoWork";
    private static readonly string CalleeSym = "M:MyNs.ServiceB.Execute";
    private static readonly string DeepSym = "M:MyNs.ServiceB.Helper";

    private readonly string _tempDir;
    private readonly BaselineStore _store;

    public BaselineStoreOutgoingRefsTests()
    {
        (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        var symbols = new[]
        {
            StorageTestHelpers.MakeSymbol(CallerSym, CallerSym, SymbolKind.Method, "src/ServiceA.cs", 5, 15),
            StorageTestHelpers.MakeSymbol(CalleeSym, CalleeSym, SymbolKind.Method, "src/ServiceB.cs", 3, 10),
            StorageTestHelpers.MakeSymbol(DeepSym,   DeepSym,   SymbolKind.Method, "src/ServiceB.cs", 12, 20),
        };
        var refs = new[]
        {
            StorageTestHelpers.MakeRef(CallerSym, CalleeSym, RefKind.Call,        "src/ServiceA.cs", 8, 8),
            StorageTestHelpers.MakeRef(CallerSym, DeepSym,   RefKind.Call,        "src/ServiceA.cs", 9, 9),
            StorageTestHelpers.MakeRef(CallerSym, DeepSym,   RefKind.Instantiate, "src/ServiceA.cs", 11, 11),
        };
        var data = StorageTestHelpers.MakeResult(symbols, refs, [FileA, FileB]);
        await _store.CreateBaselineAsync(Repo, Sha, data);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetOutgoing_MethodThatCalls_ReturnsCallees()
    {
        var refs = await _store.GetOutgoingReferencesAsync(Repo, Sha, SymbolId.From(CallerSym), null, 50);

        refs.Should().HaveCount(3);
        refs.Should().Contain(r => r.ToSymbol.Value == CalleeSym);
        refs.Should().Contain(r => r.ToSymbol.Value == DeepSym);
    }

    [Fact]
    public async Task GetOutgoing_WithKindFilter_FiltersCorrectly()
    {
        var callRefs = await _store.GetOutgoingReferencesAsync(
            Repo, Sha, SymbolId.From(CallerSym), RefKind.Call, 50);

        callRefs.Should().HaveCount(2);
        callRefs.Should().AllSatisfy(r => r.Kind.Should().Be(RefKind.Call));

        var instantiateRefs = await _store.GetOutgoingReferencesAsync(
            Repo, Sha, SymbolId.From(CallerSym), RefKind.Instantiate, 50);

        instantiateRefs.Should().HaveCount(1);
        instantiateRefs[0].Kind.Should().Be(RefKind.Instantiate);
    }

    [Fact]
    public async Task GetOutgoing_NoOutgoingRefs_ReturnsEmpty()
    {
        // CalleeSym has no outgoing refs seeded
        var refs = await _store.GetOutgoingReferencesAsync(Repo, Sha, SymbolId.From(CalleeSym), null, 50);
        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOutgoing_LimitApplied()
    {
        var refs = await _store.GetOutgoingReferencesAsync(
            Repo, Sha, SymbolId.From(CallerSym), null, limit: 1);

        refs.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetOutgoing_MultipleCallsToSameSymbol_AllReturned()
    {
        // CallerSym calls DeepSym twice (Call + Instantiate)
        var deepRefs = await _store.GetOutgoingReferencesAsync(
            Repo, Sha, SymbolId.From(CallerSym), null, 50);

        deepRefs.Where(r => r.ToSymbol.Value == DeepSym).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetOutgoing_SymbolNotInIndex_ReturnsEmpty()
    {
        var refs = await _store.GetOutgoingReferencesAsync(
            Repo, Sha, SymbolId.From("M:Unknown.Symbol"), null, 50);
        refs.Should().BeEmpty();
    }
}
