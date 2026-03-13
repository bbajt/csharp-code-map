namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

public class BaselineStoreResolutionStateTests : IDisposable
{
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));

    private readonly string _tempDir;
    private readonly BaselineStore _store;

    public BaselineStoreResolutionStateTests()
        => (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ExtractedReference MakeUnresolved(
        string toName,
        string? hint = "_svc",
        string filePath = "src/A.cs") =>
        new(
            FromSymbol: SymbolId.From("M:A.Run"),
            ToSymbol: SymbolId.Empty,
            Kind: RefKind.Call,
            FilePath: FilePath.From(filePath),
            LineStart: 5,
            LineEnd: 5,
            ResolutionState: ResolutionState.Unresolved,
            ToName: toName,
            ToContainerHint: hint);

    private async Task SeedMixedAsync()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa00001111aaaa");
        var symA = StorageTestHelpers.MakeSymbol("M:A.Run", "A.Run", SymbolKind.Method, "src/A.cs");
        var symB = StorageTestHelpers.MakeSymbol("M:B.Process", "B.Process", SymbolKind.Method, "src/A.cs");
        var symC = StorageTestHelpers.MakeSymbol("M:C.Helper", "C.Helper", SymbolKind.Method, "src/A.cs");

        // 3 resolved refs (from A.Run → B.Process / C.Helper)
        var resolved1 = StorageTestHelpers.MakeRef("M:A.Run", "M:B.Process", RefKind.Call, "src/A.cs");
        var resolved2 = StorageTestHelpers.MakeRef("M:A.Run", "M:C.Helper", RefKind.Call, "src/A.cs");
        var resolved3 = StorageTestHelpers.MakeRef("M:B.Process", "M:C.Helper", RefKind.Call, "src/A.cs");

        // 2 unresolved refs targeting B.Process conceptually (via ToName)
        // Note: these target empty string symbol_id, so we need a to_symbol_id that's valid file-wise
        // To test resolution_state filter, insert them pointing to B.Process FQN
        var unresolved1 = MakeUnresolved("Execute");
        var unresolved2 = MakeUnresolved("Helper", "_obj");

        // For unresolved refs stored in DB, to_symbol_id is empty string
        // GetReferencesAsync queries WHERE to_symbol_id = $symbol_id
        // So we can't query unresolved refs via GetReferencesAsync with an empty symbol ID easily
        // Instead store them with a known to_symbol_id for testing the filter

        // Override: make the unresolved refs point to B.Process as their "to" (empty ToSymbol in model, but we'll use B.Process as FK for DB testing)
        var unresolved1b = unresolved1 with { ToSymbol = SymbolId.From("M:B.Process") };
        var unresolved2b = unresolved2 with { ToSymbol = SymbolId.From("M:B.Process") };

        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult(
                [symA, symB, symC],
                [resolved1, resolved2, resolved3, unresolved1b, unresolved2b],
                [file]));
    }

    // ── Default state ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBaseline_ResolvedRefs_DefaultStateIsResolved()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa00001111aaaa");
        var symA = StorageTestHelpers.MakeSymbol("M:A.Run", "A.Run", SymbolKind.Method, "src/A.cs");
        var symB = StorageTestHelpers.MakeSymbol("M:B.Process", "B.Process", SymbolKind.Method, "src/A.cs");
        var refAB = StorageTestHelpers.MakeRef("M:A.Run", "M:B.Process", RefKind.Call, "src/A.cs");

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([symA, symB], [refAB], [file]));

        var refs = await _store.GetReferencesAsync(Repo, Sha, SymbolId.From("M:B.Process"), null, 10);
        refs.Should().HaveCount(1);
        refs[0].ResolutionState.Should().Be(ResolutionState.Resolved);
    }

    // ── Unresolved refs ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBaseline_UnresolvedRefs_StoresWithToNameAndHint()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa00001111aaaa");
        var symA = StorageTestHelpers.MakeSymbol("M:A.Run", "A.Run", SymbolKind.Method, "src/A.cs");
        var symB = StorageTestHelpers.MakeSymbol("M:B.Process", "B.Process", SymbolKind.Method, "src/A.cs");

        var unresolvedRef = new ExtractedReference(
            FromSymbol: SymbolId.From("M:A.Run"),
            ToSymbol: SymbolId.From("M:B.Process"),  // keying for lookup
            Kind: RefKind.Call,
            FilePath: FilePath.From("src/A.cs"),
            LineStart: 5,
            LineEnd: 5,
            ResolutionState: ResolutionState.Unresolved,
            ToName: "Execute",
            ToContainerHint: "_svc");

        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([symA, symB], [unresolvedRef], [file]));

        var refs = await _store.GetReferencesAsync(Repo, Sha, SymbolId.From("M:B.Process"), null, 10);
        refs.Should().HaveCount(1);
        refs[0].ResolutionState.Should().Be(ResolutionState.Unresolved);
        refs[0].ToName.Should().Be("Execute");
        refs[0].ToContainerHint.Should().Be("_svc");
    }

    // ── Resolution state filter ──────────────────────────────────────────────

    [Fact]
    public async Task QueryRefs_NoFilter_ReturnsBothStates()
    {
        await SeedMixedAsync();

        // B.Process is referenced by: A.Run (resolved) + A.Run (unresolved x2)
        var refs = await _store.GetReferencesAsync(Repo, Sha, SymbolId.From("M:B.Process"), null, 50);
        refs.Should().HaveCount(3); // 1 resolved + 2 unresolved
    }

    [Fact]
    public async Task QueryRefs_FilterResolved_ReturnsOnlyResolved()
    {
        await SeedMixedAsync();

        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("M:B.Process"), null, 50,
            resolutionState: ResolutionState.Resolved);

        refs.Should().HaveCount(1);
        refs.Should().AllSatisfy(r => r.ResolutionState.Should().Be(ResolutionState.Resolved));
    }

    [Fact]
    public async Task QueryRefs_FilterUnresolved_ReturnsOnlyUnresolved()
    {
        await SeedMixedAsync();

        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("M:B.Process"), null, 50,
            resolutionState: ResolutionState.Unresolved);

        refs.Should().HaveCount(2);
        refs.Should().AllSatisfy(r => r.ResolutionState.Should().Be(ResolutionState.Unresolved));
    }

    [Fact]
    public async Task QueryRefs_UnresolvedRef_HasEmptyToSymbolId()
    {
        var file = StorageTestHelpers.MakeFile("src/A.cs", "aaaa00001111aaaa");
        var symA = StorageTestHelpers.MakeSymbol("M:A.Run", "A.Run", SymbolKind.Method, "src/A.cs");
        var symB = StorageTestHelpers.MakeSymbol("M:B.Process", "B.Process", SymbolKind.Method, "src/A.cs");

        // Insert unresolved ref with SymbolId.Empty as ToSymbol
        var unresolvedRef = new ExtractedReference(
            FromSymbol: SymbolId.From("M:A.Run"),
            ToSymbol: SymbolId.Empty,
            Kind: RefKind.Call,
            FilePath: FilePath.From("src/A.cs"),
            LineStart: 5,
            LineEnd: 5,
            ResolutionState: ResolutionState.Unresolved,
            ToName: "Execute",
            ToContainerHint: "_svc");

        // Note: to_symbol_id is empty string here; this ref won't appear in GetReferencesAsync
        // (which queries by to_symbol_id). We verify via raw SQL.
        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult([symA, symB], [unresolvedRef], [file]));

        // Verify via outgoing refs that the ref is stored with empty to_symbol
        var outgoing = await _store.GetOutgoingReferencesAsync(Repo, Sha, SymbolId.From("M:A.Run"), null, 10);
        outgoing.Should().ContainSingle(r => r.ResolutionState == ResolutionState.Unresolved);
        outgoing.Single(r => r.ResolutionState == ResolutionState.Unresolved)
            .ToSymbol.Value.Should().BeEmpty();
    }
}
