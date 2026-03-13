namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public class OverlayStoreStableIdTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("overlay-stable-id-test");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-stable-id");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));

    private readonly string _tempDir;
    private readonly OverlayStore _store;

    public OverlayStoreStableIdTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var factory = new OverlayDbFactory(_tempDir, NullLogger<OverlayDbFactory>.Instance);
        _store = new OverlayStore(factory, NullLogger<OverlayStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private async Task SeedAsync(IReadOnlyList<SymbolCard>? symbols = null, IReadOnlyList<ExtractedReference>? refs = null)
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta(
            symbols: symbols,
            refs: refs);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);
    }

    // ── stable_id stored and returned from GetOverlaySymbolAsync ─────────────

    [Fact]
    public async Task GetOverlaySymbolAsync_SymbolWithStableId_ReturnsStableId()
    {
        var stableId = new StableId("sym_aabb11223344ccdd");
        var symWithId = OverlayTestHelpers.MakeSymbol("T:TestNs.Foo") with { StableId = stableId };

        await SeedAsync(symbols: [symWithId]);

        var card = await _store.GetOverlaySymbolAsync(Repo, Workspace, SymbolId.From("T:TestNs.Foo"));
        card.Should().NotBeNull();
        card!.StableId.Should().Be(stableId);
    }

    // ── GetSymbolByStableIdAsync in overlay ──────────────────────────────────

    [Fact]
    public async Task GetSymbolByStableIdAsync_ExistingId_ReturnsCard()
    {
        var stableId = new StableId("sym_0011223344556677");
        var sym = OverlayTestHelpers.MakeSymbol("M:TestNs.Bar.Run") with { StableId = stableId };

        await SeedAsync(symbols: [sym]);

        var result = await _store.GetSymbolByStableIdAsync(Repo, Workspace, stableId);
        result.Should().NotBeNull();
        result!.SymbolId.Value.Should().Be("M:TestNs.Bar.Run");
        result.StableId.Should().Be(stableId);
    }

    [Fact]
    public async Task GetSymbolByStableIdAsync_NonExistentId_ReturnsNull()
    {
        await SeedAsync();

        var result = await _store.GetSymbolByStableIdAsync(
            Repo, Workspace, new StableId("sym_ffffffffffffffff"));
        result.Should().BeNull();
    }
}
