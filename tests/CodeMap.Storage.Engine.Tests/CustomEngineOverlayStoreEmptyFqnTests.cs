namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// Regression tests for the workspace-mode <c>-32603 Internal error</c> reported 2026-05-27
/// (see <c>ByTech.MP-CPU/.errors/</c>). An overlay-new symbol whose source <see cref="SymbolId"/>
/// was <see cref="SymbolId.Empty"/> is stored with an empty FQN string but still has its name
/// tokens indexed. A search that matched the token then reached <c>SymbolId.From("")</c>, which
/// throws <see cref="System.ArgumentException"/> and escaped to the MCP layer as JSON-RPC -32603.
///
/// Two guards: the read path skips empty-FQN overlay symbols (so overlays already on disk are
/// safe without re-indexing), and the write path drops id-less symbols so they never enter.
/// </summary>
public sealed class CustomEngineOverlayStoreEmptyFqnTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-ovl-emptyfqn-{Guid.NewGuid():N}");
    private CustomSymbolStore _symbolStore = null!;
    private CustomEngineOverlayStore _overlayStore = null!;

    private const string RepoName = "rt-repo";
    private static readonly RepoId Repo = RepoId.From(RepoName);
    private static readonly WorkspaceId Ws = WorkspaceId.From("rt-ws");
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeBaseDir = Path.Combine(_tempDir, "store");

        // Build a baseline where CustomSymbolStore expects it: <base>/<repoId>/baselines/<sha>.
        // Sha must match TestData.CreateTestInput()'s CommitSha so GetOrOpenBaseline finds it.
        var builder = new EngineBaselineBuilder(Path.Combine(storeBaseDir, RepoName));
        var result = await builder.BuildAsync(TestData.CreateTestInput(), CancellationToken.None);
        result.Success.Should().BeTrue();

        _symbolStore = new CustomSymbolStore(storeBaseDir);
        _overlayStore = new CustomEngineOverlayStore(_symbolStore, storeBaseDir);
        await _overlayStore.CreateOverlayAsync(Repo, Ws, Sha);
    }

    public ValueTask DisposeAsync()
    {
        _symbolStore?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SearchOverlay_SymbolWithEmptyFqn_DoesNotThrow_AndReturnsOnlyValidHits()
    {
        // Inject directly into the overlay to simulate a pre-fix overlay already on disk:
        // a symbol searchable by the token "reg" but with FqnStringId = 0 (resolves to "").
        var overlay = _symbolStore.TryGetOverlay(Repo.Value, Ws.Value)!;

        var badStableSid = overlay.InternStringInternal("sym_overlay_bad");
        var badDisplaySid = overlay.InternStringInternal("Reg");
        var badTokensSid = overlay.InternStringInternal("reg");
        // FqnStringId intentionally 0 → ResolveString(0) == "" → SymbolId.From("") would throw.
        var bad = new SymbolRecord(
            symbolIntId: -1, stableIdStringId: badStableSid, fqnStringId: 0,
            displayNameStringId: badDisplaySid, namespaceStringId: 0,
            containerIntId: 0, fileIntId: 0, projectIntId: 0,
            kind: 1, accessibility: 7, flags: 0, spanStart: 1, spanEnd: 10,
            nameTokensStringId: badTokensSid, signatureHash: 0);

        var goodStableSid = overlay.InternStringInternal("sym_overlay_good");
        var goodFqnSid = overlay.InternStringInternal("T:MyApp.RegService");
        var goodDisplaySid = overlay.InternStringInternal("RegService");
        var goodTokensSid = overlay.InternStringInternal("regservice reg");
        var good = new SymbolRecord(
            symbolIntId: -2, stableIdStringId: goodStableSid, fqnStringId: goodFqnSid,
            displayNameStringId: goodDisplaySid, namespaceStringId: 0,
            containerIntId: 0, fileIntId: 0, projectIntId: 0,
            kind: 1, accessibility: 7, flags: 0, spanStart: 1, spanEnd: 10,
            nameTokensStringId: goodTokensSid, signatureHash: 0);

        using (var batch = overlay.BeginBatch())
        {
            batch.UpsertSymbol(bad, ["reg"]);
            batch.UpsertSymbol(good, ["regservice", "reg"]);
            await batch.CommitAsync();
        }

        IReadOnlyList<SymbolSearchHit> hits = null!;
        var act = async () => hits = await _overlayStore.SearchOverlaySymbolsAsync(Repo, Ws, "reg", null, 10);

        await act.Should().NotThrowAsync("an empty-FQN overlay symbol must be skipped, not crash the query");
        hits.Should().ContainSingle().Which.FullyQualifiedName.Should().Be("T:MyApp.RegService");
    }

    [Fact]
    public async Task ApplyDelta_SymbolWithEmptySymbolId_IsSkipped_NeverStored()
    {
        var idLess = SymbolCard.CreateMinimal(
            SymbolId.From("T:MyApp.Placeholder"), "global::MyApp.RegThing", SymbolKind.Class,
            "public class RegThing", "MyApp", FilePath.From("src/App/RegThing.cs"),
            1, 5, "public", Confidence.High)
            with { SymbolId = SymbolId.Empty };

        var delta = new OverlayDelta(
            ReindexedFiles: [],
            AddedOrUpdatedSymbols: [idLess],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1);

        var act = async () => await _overlayStore.ApplyDeltaAsync(Repo, Ws, delta);
        await act.Should().NotThrowAsync();

        // The id-less symbol was the only thing in the delta — nothing should have been stored.
        var overlay = _symbolStore.TryGetOverlay(Repo.Value, Ws.Value)!;
        overlay.GetOverlayNewSymbols().Should().BeEmpty(
            "a symbol with an empty SymbolId is unaddressable and must be dropped at write time");
    }
}
