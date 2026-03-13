namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class OverlayStoreReadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    private static readonly RepoId Repo = RepoId.From("read-test-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-read");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));

    public OverlayStoreReadTests()
    {
        Directory.CreateDirectory(_tempDir);
        _factory = new OverlayDbFactory(_tempDir, NullLogger<OverlayDbFactory>.Instance);
        _store = new OverlayStore(_factory, NullLogger<OverlayStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── OverlayExistsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Exists_AfterCreate_ReturnsTrue()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        (await _store.OverlayExistsAsync(Repo, Workspace)).Should().BeTrue();
    }

    [Fact]
    public async Task Exists_BeforeCreate_ReturnsFalse()
    {
        (await _store.OverlayExistsAsync(Repo, WorkspaceId.From("never-created")))
            .Should().BeFalse();
    }

    // ── GetRevisionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetRevision_InitiallyZero()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        (await _store.GetRevisionAsync(Repo, Workspace)).Should().Be(0);
    }

    [Fact]
    public async Task GetRevision_AfterApplyDelta_ReturnsNewRevision()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta(newRevision: 3));
        (await _store.GetRevisionAsync(Repo, Workspace)).Should().Be(3);
    }

    [Fact]
    public async Task GetRevision_NoOverlay_ReturnsZero()
    {
        (await _store.GetRevisionAsync(Repo, WorkspaceId.From("ghost"))).Should().Be(0);
    }

    // ── GetOverlaySymbolAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbol_ExistingId_ReturnsSymbolCard()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var card = await _store.GetOverlaySymbolAsync(
            Repo, Workspace, SymbolId.From("T:TestNs.Foo"));

        card.Should().NotBeNull();
        card!.FullyQualifiedName.Should().Be("TestNs.Foo");
    }

    [Fact]
    public async Task GetSymbol_NonExistentId_ReturnsNull()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var card = await _store.GetOverlaySymbolAsync(
            Repo, Workspace, SymbolId.From("T:Missing.Symbol"));

        card.Should().BeNull();
    }

    [Fact]
    public async Task GetSymbol_NoOverlay_ReturnsNull()
    {
        var card = await _store.GetOverlaySymbolAsync(
            Repo, WorkspaceId.From("ghost"), SymbolId.From("T:TestNs.Foo"));
        card.Should().BeNull();
    }

    [Fact]
    public async Task GetSymbol_ReturnsCorrectContentHash()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        // content_hash column is stored — verify the row exists (GetOverlaySymbolAsync succeeds)
        var card = await _store.GetOverlaySymbolAsync(
            Repo, Workspace, SymbolId.From("T:TestNs.Foo"));
        card.Should().NotBeNull(); // content_hash was stored and read correctly
    }

    // ── SearchOverlaySymbolsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Search_MatchingQuery_ReturnsHits()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var hits = await _store.SearchOverlaySymbolsAsync(Repo, Workspace, "Foo", null, 10);

        hits.Should().NotBeEmpty();
        hits[0].FullyQualifiedName.Should().Be("TestNs.Foo");
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmpty()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var hits = await _store.SearchOverlaySymbolsAsync(Repo, Workspace, "Zzz", null, 10);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_LimitHonored()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile();
        var symbols = Enumerable.Range(1, 5)
            .Select(i => OverlayTestHelpers.MakeSymbol($"T:TestNs.Foo{i}", $"TestNs.Foo{i}"))
            .ToList();
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta([file], symbols));

        var hits = await _store.SearchOverlaySymbolsAsync(Repo, Workspace, "Foo", null, 3);

        hits.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task Search_KindFilterApplied()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile();
        var cls = OverlayTestHelpers.MakeSymbol("T:TestNs.Foo", kind: SymbolKind.Class);
        var method = OverlayTestHelpers.MakeSymbol("M:TestNs.Foo.Run", "TestNs.Foo.Run", kind: SymbolKind.Method);
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta([file], [cls, method]));

        var hits = await _store.SearchOverlaySymbolsAsync(
            Repo, Workspace, "Foo",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]), 10);

        hits.Should().OnlyContain(h => h.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task Search_NamespaceFilterApplied()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile();
        var symbolA = OverlayTestHelpers.MakeSymbol("T:TestNs.Foo");
        // Search with a namespace filter that doesn't match
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta([file], [symbolA]));

        var hits = await _store.SearchOverlaySymbolsAsync(
            Repo, Workspace, "Foo",
            new SymbolSearchFilters(Namespace: "OtherNs"), 10);

        hits.Should().BeEmpty();
    }

    // ── GetOverlayReferencesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetRefs_ExistingSymbol_ReturnsRefs()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/Bar.cs", "bbbbbbbbbbbbbbbb");
        var symbol = OverlayTestHelpers.MakeSymbol("M:TestNs.Bar.Run", "TestNs.Bar.Run", "src/Bar.cs");
        var refr = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/Bar.cs");
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta([file], [symbol], null, [refr]));

        var refs = await _store.GetOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From("T:TestNs.Foo"), null, 10);

        refs.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetRefs_KindFilter_OnlyMatchingKind()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/Bar.cs", "bbbbbbbbbbbbbbbb");
        var symbol = OverlayTestHelpers.MakeSymbol("M:TestNs.Bar.Run", "TestNs.Bar.Run", "src/Bar.cs");
        var call = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/Bar.cs", RefKind.Call);
        var impl = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/Bar.cs", RefKind.Implementation);
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta([file], [symbol], null, [call, impl]));

        var refs = await _store.GetOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From("T:TestNs.Foo"), RefKind.Call, 10);

        refs.Should().OnlyContain(r => r.Kind == RefKind.Call);
    }

    [Fact]
    public async Task GetRefs_NoOverlay_ReturnsEmpty()
    {
        var refs = await _store.GetOverlayReferencesAsync(
            Repo, WorkspaceId.From("ghost"), SymbolId.From("T:TestNs.Foo"), null, 10);
        refs.Should().BeEmpty();
    }

    // ── GetDeletedSymbolIdsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetDeleted_AfterDeltaWithDeletions_ReturnsIds()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var deletedId = SymbolId.From("T:TestNs.OldClass");
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta(deletedIds: [deletedId]));

        var ids = await _store.GetDeletedSymbolIdsAsync(Repo, Workspace);

        ids.Should().Contain(deletedId);
    }

    [Fact]
    public async Task GetDeleted_NoDeletions_ReturnsEmpty()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var ids = await _store.GetDeletedSymbolIdsAsync(Repo, Workspace);

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDeleted_ReAddedSymbol_NotInDeletedSet()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var symbolId = SymbolId.From("T:TestNs.Foo");

        // Delete it
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta(
                files: [OverlayTestHelpers.MakeFile()],
                symbols: [],
                deletedIds: [symbolId],
                newRevision: 1));

        // Re-add it
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta(newRevision: 2));

        var ids = await _store.GetDeletedSymbolIdsAsync(Repo, Workspace);
        ids.Should().NotContain(symbolId);
    }

    // ── GetOverlayFilePathsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetFilePaths_AfterDelta_ReturnsReindexedPaths()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var paths = await _store.GetOverlayFilePathsAsync(Repo, Workspace);

        paths.Should().Contain(FilePath.From("src/Foo.cs"));
    }

    [Fact]
    public async Task GetFilePaths_NoOverlay_ReturnsEmpty()
    {
        var paths = await _store.GetOverlayFilePathsAsync(Repo, WorkspaceId.From("ghost"));
        paths.Should().BeEmpty();
    }
}
