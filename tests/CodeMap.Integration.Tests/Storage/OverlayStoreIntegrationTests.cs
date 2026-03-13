namespace CodeMap.Integration.Tests.Storage;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Storage;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Integration")]
public sealed class OverlayStoreIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    private static readonly RepoId Repo = RepoId.From("integration-overlay-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-integration");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));

    public OverlayStoreIntegrationTests()
    {
        Directory.CreateDirectory(_tempDir);
        _factory = new OverlayDbFactory(_tempDir, NullLogger<OverlayDbFactory>.Instance);
        _store = new OverlayStore(_factory, NullLogger<OverlayStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RoundTrip_CreateAndApplyDelta_SymbolsReadable()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta();
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        var card = await _store.GetOverlaySymbolAsync(
            Repo, Workspace, SymbolId.From("T:TestNs.Foo"));

        card.Should().NotBeNull();
        card!.FullyQualifiedName.Should().Be("TestNs.Foo");
    }

    [Fact]
    public async Task RoundTrip_CreateAndSearch_FtsFindsOverlaySymbol()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        var hits = await _store.SearchOverlaySymbolsAsync(Repo, Workspace, "Foo", null, 10);

        hits.Should().NotBeEmpty();
        hits[0].FullyQualifiedName.Should().Be("TestNs.Foo");
    }

    [Fact]
    public async Task RoundTrip_ApplyDeltaTwice_SecondDeltaReplacesFirst()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // First delta: symbol A
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta(
            files: [OverlayTestHelpers.MakeFile()],
            symbols: [OverlayTestHelpers.MakeSymbol("T:TestNs.A", "TestNs.A")],
            newRevision: 1));

        // Second delta for same file: only symbol B
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta(
            files: [OverlayTestHelpers.MakeFile()], // same file_id
            symbols: [OverlayTestHelpers.MakeSymbol("T:TestNs.B", "TestNs.B")],
            newRevision: 2));

        // Symbol A should be gone (same file was re-indexed)
        var cardA = await _store.GetOverlaySymbolAsync(Repo, Workspace, SymbolId.From("T:TestNs.A"));
        var cardB = await _store.GetOverlaySymbolAsync(Repo, Workspace, SymbolId.From("T:TestNs.B"));

        cardA.Should().BeNull("symbol A was in the same file that was re-indexed");
        cardB.Should().NotBeNull();
    }

    [Fact]
    public async Task RoundTrip_DeletedSymbol_AppearsInDeletedSet()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var deletedId = SymbolId.From("T:TestNs.Removed");

        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta(
            deletedIds: [deletedId]));

        var deleted = await _store.GetDeletedSymbolIdsAsync(Repo, Workspace);
        deleted.Should().Contain(deletedId);
    }

    [Fact]
    public async Task RoundTrip_ResetThenRead_AllEmpty()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta(
            deletedIds: [SymbolId.From("T:TestNs.Old")]));

        await _store.ResetOverlayAsync(Repo, Workspace);

        var card = await _store.GetOverlaySymbolAsync(Repo, Workspace, SymbolId.From("T:TestNs.Foo"));
        var deleted = await _store.GetDeletedSymbolIdsAsync(Repo, Workspace);
        var paths = await _store.GetOverlayFilePathsAsync(Repo, Workspace);
        var rev = await _store.GetRevisionAsync(Repo, Workspace);

        card.Should().BeNull();
        deleted.Should().BeEmpty();
        paths.Should().BeEmpty();
        rev.Should().Be(0);
    }

    [Fact]
    public async Task RoundTrip_DeleteOverlay_FileRemoved()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var path = _factory.GetDbPath(Repo, Workspace);
        File.Exists(path).Should().BeTrue();

        await _store.DeleteOverlayAsync(Repo, Workspace);

        File.Exists(path).Should().BeFalse();
    }
}
