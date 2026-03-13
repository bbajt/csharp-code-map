namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

public class BaselineStoreStableIdTests : IDisposable
{
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));

    private readonly string _tempDir;
    private readonly BaselineStore _store;

    public BaselineStoreStableIdTests()
        => (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Stable ID persisted in symbols ───────────────────────────────────────

    [Fact]
    public async Task GetSymbolAsync_SymbolWithStableId_ReturnsStableId()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        var sym = StorageTestHelpers.MakeSymbol("T:Ns.MyClass", "Ns.MyClass", SymbolKind.Class, "src/A.cs");
        var stableId = new StableId("sym_aabbccdd11223344");
        var symWithId = sym with { StableId = stableId };

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([symWithId], [], [file]));

        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("T:Ns.MyClass"));
        card.Should().NotBeNull();
        card!.StableId.Should().Be(stableId);
    }

    [Fact]
    public async Task GetSymbolAsync_SymbolWithoutStableId_ReturnsNullStableId()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        var sym = StorageTestHelpers.MakeSymbol("T:Ns.MyClass", "Ns.MyClass", SymbolKind.Class, "src/A.cs");

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([sym], [], [file]));

        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("T:Ns.MyClass"));
        card.Should().NotBeNull();
        card!.StableId.Should().BeNull();
    }

    // ── GetSymbolByStableIdAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolByStableIdAsync_ExistingId_ReturnsCard()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        var stableId = new StableId("sym_0011223344556677");
        var sym = StorageTestHelpers.MakeSymbol("M:Ns.Foo.Bar", "Ns.Foo.Bar", SymbolKind.Method, "src/A.cs");
        var symWithId = sym with { StableId = stableId };

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([symWithId], [], [file]));

        var result = await _store.GetSymbolByStableIdAsync(Repo, Sha, stableId);
        result.Should().NotBeNull();
        result!.SymbolId.Value.Should().Be("M:Ns.Foo.Bar");
        result.StableId.Should().Be(stableId);
    }

    [Fact]
    public async Task GetSymbolByStableIdAsync_NonExistentId_ReturnsNull()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        var sym = StorageTestHelpers.MakeSymbol("T:Ns.MyClass", "Ns.MyClass", SymbolKind.Class, "src/A.cs");

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([sym], [], [file]));

        var result = await _store.GetSymbolByStableIdAsync(Repo, Sha, new StableId("sym_ffffffffffffffff"));
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSymbolByStableIdAsync_EmptyStableId_ReturnsNull()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([], [], [file]));

        var result = await _store.GetSymbolByStableIdAsync(Repo, Sha, StableId.Empty);
        result.Should().BeNull();
    }

    // ── GetSymbolsByFileAsync includes stable_id ─────────────────────────────

    [Fact]
    public async Task GetSymbolsByFileAsync_SymbolsWithStableId_StableIdPopulated()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        var sid1 = new StableId("sym_0000000011111111");
        var sid2 = new StableId("sym_2222222233333333");
        var sym1 = StorageTestHelpers.MakeSymbol("T:Ns.Foo", "Ns.Foo", SymbolKind.Class, "src/A.cs") with { StableId = sid1 };
        var sym2 = StorageTestHelpers.MakeSymbol("M:Ns.Foo.Run", "Ns.Foo.Run", SymbolKind.Method, "src/A.cs") with { StableId = sid2 };

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([sym1, sym2], [], [file]));

        var cards = await _store.GetSymbolsByFileAsync(Repo, Sha, FilePath.From("src/A.cs"));
        cards.Should().HaveCount(2);
        cards.Should().Contain(c => c.StableId == sid1);
        cards.Should().Contain(c => c.StableId == sid2);
    }

    // ── Refs store stable from/to ids ───────────────────────────────────────

    [Fact]
    public async Task CreateBaseline_RefsWithStableIds_Persisted()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa11110000bbbb");
        var sym1 = StorageTestHelpers.MakeSymbol("M:Ns.A.Go", "Ns.A.Go", SymbolKind.Method, "src/A.cs");
        var sym2 = StorageTestHelpers.MakeSymbol("M:Ns.B.Do", "Ns.B.Do", SymbolKind.Method, "src/A.cs");

        var sfid = new StableId("sym_aaaa000011112222");
        var stid = new StableId("sym_bbbb111122223333");
        var refWithIds = new ExtractedReference(
            FromSymbol: SymbolId.From("M:Ns.A.Go"),
            ToSymbol: SymbolId.From("M:Ns.B.Do"),
            Kind: RefKind.Call,
            FilePath: FilePath.From("src/A.cs"),
            LineStart: 5,
            LineEnd: 5,
            StableFromId: sfid,
            StableToId: stid);

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([sym1, sym2], [refWithIds], [file]));

        // Verify by reading back - refs themselves don't surface stable IDs in the current API
        // but we verify the insertion didn't fail and refs are stored
        var refs = await _store.GetReferencesAsync(Repo, Sha, SymbolId.From("M:Ns.B.Do"), null, 10);
        refs.Should().HaveCount(1);
        refs[0].FromSymbol.Value.Should().Be("M:Ns.A.Go");
    }
}
