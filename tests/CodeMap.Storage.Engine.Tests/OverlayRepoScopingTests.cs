namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-03 T02 regression: overlay state was keyed by <c>workspaceId</c> only
/// (<c>store/overlays/&lt;ws&gt;/</c>) while <c>WorkspaceManager</c> keys workspaces by
/// <c>(RepoId, WorkspaceId)</c>. The same workspace id in two repos — CLAUDE.MD tells every
/// agent to use <c>"session"</c> — shared one <see cref="EngineOverlay"/> bound to the first
/// repo's baseline, and the last <c>CreateOverlayAsync</c> won the repo/commit mapping.
/// Overlays now live at <c>&lt;store&gt;/&lt;repoId&gt;/overlays/&lt;ws&gt;/</c> (ADR-043).
/// Tests use only the <see cref="Core.Interfaces.IOverlayStore"/> surface so they compile
/// against the pre-fix code.
/// </summary>
public sealed class OverlayRepoScopingTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-ovl-scope-{Guid.NewGuid():N}");
    private string _storeBaseDir = null!;
    private CustomSymbolStore _symbolStore = null!;
    private CustomEngineOverlayStore _overlayStore = null!;
    private readonly List<CustomSymbolStore> _extraStores = [];

    // Two repos at the SAME commit — e.g. two remote-less clones (distinct local-<hash> ids).
    private static readonly RepoId RepoA = RepoId.From("repo-a");
    private static readonly RepoId RepoB = RepoId.From("repo-b");
    private static readonly WorkspaceId Session = WorkspaceId.From("session");
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");

    public async ValueTask InitializeAsync()
    {
        _storeBaseDir = Path.Combine(_tempDir, "store");

        // Repo A: the standard MyApp baseline (T:MyApp.Foo, …).
        var a = await new EngineBaselineBuilder(Path.Combine(_storeBaseDir, RepoA.Value))
            .BuildAsync(TestData.CreateTestInput(), CancellationToken.None);
        a.Success.Should().BeTrue();

        // Repo B: different symbols (T:OtherApp.Qux only), same commit.
        var inputB = TestData.CreateTestInput() with
        {
            Symbols =
            [
                SymbolCard.CreateMinimal(SymbolId.From("T:OtherApp.Qux"), "global::OtherApp.Qux", SymbolKind.Class,
                    "public class Qux", "OtherApp", FilePath.From("src/App/Foo.cs"), 1, 10, "public", Confidence.High),
            ],
            References = [],
            Facts = [],
        };
        var b = await new EngineBaselineBuilder(Path.Combine(_storeBaseDir, RepoB.Value))
            .BuildAsync(inputB, CancellationToken.None);
        b.Success.Should().BeTrue();

        _symbolStore = new CustomSymbolStore(_storeBaseDir);
        _overlayStore = new CustomEngineOverlayStore(_symbolStore, _storeBaseDir);
    }

    public ValueTask DisposeAsync()
    {
        _symbolStore?.Dispose();
        foreach (var s in _extraStores) s.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SameWorkspaceId_TwoRepos_OverlaysAreIndependent()
    {
        await CreateBothAsync();

        await _overlayStore.ApplyDeltaAsync(RepoA, Session, AddSymbolDelta("T:MyApp.Zed"));

        (await SearchAsync(RepoA, "zed")).Should().ContainSingle()
            .Which.FullyQualifiedName.Should().Be("T:MyApp.Zed");
        (await SearchAsync(RepoB, "zed")).Should().BeEmpty(
            "repo B's \"session\" must not see a symbol added to repo A's \"session\"");
        (await _overlayStore.GetRevisionAsync(RepoB, Session)).Should().Be(0);
    }

    [Fact]
    public async Task SameWorkspaceId_TwoRepos_EachResolvesItsOwnBaseline()
    {
        await CreateBothAsync();

        // Tombstones resolve the deleted symbol against the overlay's baseline reader: A's
        // delete must be resolved in A's baseline (which has T:MyApp.Foo), B's in B's.
        await _overlayStore.ApplyDeltaAsync(RepoA, Session, DeleteSymbolDelta("T:MyApp.Foo"));
        await _overlayStore.ApplyDeltaAsync(RepoB, Session, DeleteSymbolDelta("T:OtherApp.Qux"));

        (await _overlayStore.GetDeletedSymbolIdsAsync(RepoA, Session))
            .Should().BeEquivalentTo([SymbolId.From("T:MyApp.Foo")]);
        (await _overlayStore.GetDeletedSymbolIdsAsync(RepoB, Session))
            .Should().BeEquivalentTo([SymbolId.From("T:OtherApp.Qux")]);
    }

    [Fact]
    public async Task DeleteOverlay_RepoA_LeavesRepoBIntact()
    {
        await CreateBothAsync();
        await _overlayStore.ApplyDeltaAsync(RepoB, Session, AddSymbolDelta("T:OtherApp.Zed"));

        await _overlayStore.DeleteOverlayAsync(RepoA, Session);

        (await _overlayStore.OverlayExistsAsync(RepoA, Session)).Should().BeFalse();
        (await _overlayStore.OverlayExistsAsync(RepoB, Session)).Should().BeTrue();
        (await SearchAsync(RepoB, "zed")).Should().ContainSingle();
    }

    [Fact]
    public async Task Reset_RepoA_LeavesRepoBIntact()
    {
        await CreateBothAsync();
        await _overlayStore.ApplyDeltaAsync(RepoA, Session, AddSymbolDelta("T:MyApp.Zed"));
        await _overlayStore.ApplyDeltaAsync(RepoB, Session, AddSymbolDelta("T:OtherApp.Zed"));

        await _overlayStore.ResetOverlayAsync(RepoA, Session);

        (await SearchAsync(RepoA, "zed")).Should().BeEmpty();
        (await SearchAsync(RepoB, "zed")).Should().ContainSingle()
            .Which.FullyQualifiedName.Should().Be("T:OtherApp.Zed");
    }

    [Fact]
    public async Task OverlayDirectory_IsUnderRepoStore()
    {
        await CreateBothAsync();
        await _overlayStore.ApplyDeltaAsync(RepoA, Session, AddSymbolDelta("T:MyApp.Zed"));

        Directory.Exists(Path.Combine(_storeBaseDir, RepoA.Value, "overlays", Session.Value)).Should().BeTrue();
        Directory.Exists(Path.Combine(_storeBaseDir, RepoB.Value, "overlays", Session.Value)).Should().BeTrue();
        Directory.Exists(Path.Combine(_storeBaseDir, "overlays")).Should().BeFalse(
            "the legacy flat store/overlays/<ws> layout is no longer written");
    }

    [Fact]
    public async Task SameWorkspaceId_TwoRepos_TwoStoreInstances_BothCreate()
    {
        // In-process stand-in for two daemons on one CODEMAP_HOME: each store instance opens
        // its own overlay WAL. Pre-fix both mapped to store/overlays/session/ and the second
        // open hit the first's exclusive WAL handle (F3 → WORKSPACE_IN_USE for unrelated repos).
        await _overlayStore.CreateOverlayAsync(RepoA, Session, Sha);

        var second = new CustomSymbolStore(_storeBaseDir);
        _extraStores.Add(second);
        var secondOverlays = new CustomEngineOverlayStore(second, _storeBaseDir);

        var act = async () => await secondOverlays.CreateOverlayAsync(RepoB, Session, Sha);
        await act.Should().NotThrowAsync("different repos never share an overlay WAL");

        await secondOverlays.ApplyDeltaAsync(RepoB, Session, AddSymbolDelta("T:OtherApp.Zed"));
        (await secondOverlays.SearchOverlaySymbolsAsync(RepoB, Session, "zed", null, 10)).Should().ContainSingle();
        (await SearchAsync(RepoA, "zed")).Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task CreateBothAsync()
    {
        await _overlayStore.CreateOverlayAsync(RepoA, Session, Sha);
        await _overlayStore.CreateOverlayAsync(RepoB, Session, Sha);
    }

    private Task<IReadOnlyList<SymbolSearchHit>> SearchAsync(RepoId repo, string query)
        => _overlayStore.SearchOverlaySymbolsAsync(repo, Session, query, null, 10);

    private static OverlayDelta AddSymbolDelta(string symbolId)
    {
        var simple = symbolId[(symbolId.LastIndexOf('.') + 1)..];
        var ns = symbolId[2..symbolId.LastIndexOf('.')];
        var card = SymbolCard.CreateMinimal(SymbolId.From(symbolId), $"global::{symbolId[2..]}", SymbolKind.Class,
            $"public class {simple}", ns, FilePath.From($"src/App/{simple}.cs"), 1, 5, "public", Confidence.High);
        return new OverlayDelta(
            ReindexedFiles: [],
            AddedOrUpdatedSymbols: [card],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1);
    }

    private static OverlayDelta DeleteSymbolDelta(string symbolId) => new(
        ReindexedFiles: [],
        AddedOrUpdatedSymbols: [],
        DeletedSymbolIds: [SymbolId.From(symbolId)],
        AddedOrUpdatedReferences: [],
        DeletedReferenceFiles: [],
        NewRevision: 1);
}
