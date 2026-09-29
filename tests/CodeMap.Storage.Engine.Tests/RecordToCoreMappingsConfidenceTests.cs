namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies that <see cref="RecordToCoreMappings.ToSymbolCard"/> downgrades
/// <see cref="Confidence"/> to <see cref="Confidence.Low"/> for symbols whose
/// stored <c>FileIntId</c> is 0 — the syntactic-fallback sentinel. Such symbols
/// render their <c>FilePath</c> as <c>"unknown"</c>; pre-fix the mapper still
/// re-stamped them as <see cref="Confidence.High"/>, hiding the degraded
/// metadata from MCP callers. Symbols with a real file remain High.
///
/// The unknown-file case is exercised through the overlay store. The baseline
/// builder filters out file-less symbols at write time (EngineBaselineBuilder.cs:63),
/// so the realistic syntactic-fallback path runs through the overlay's hand-written
/// record-injection layer, which mirrors what extraction does when a project fails
/// to compile and only syntactic walks are available.
/// </summary>
public sealed class RecordToCoreMappingsConfidenceTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-conf-{Guid.NewGuid():N}");
    private CustomSymbolStore _symbolStore = null!;
    private CustomEngineOverlayStore _overlayStore = null!;

    private const string RepoName = "conf-repo";
    private static readonly RepoId Repo = RepoId.From(RepoName);
    private static readonly WorkspaceId Ws = WorkspaceId.From("conf-ws");
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeBaseDir = Path.Combine(_tempDir, "store");

        // Build a real baseline so the overlay has something to layer onto. Reuses
        // the shared fixture from TestData.CreateTestInput() so the high-confidence
        // assertion runs against a known well-formed symbol.
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
    public async Task ToSymbolCard_BaselineSymbolWithRealFile_KeepsConfidenceHigh()
    {
        // T:MyApp.Foo is bound to src/App/Foo.cs in the baseline fixture, so its
        // SymbolRecord has FileIntId > 0 — the standard semantic-extraction case.
        var card = await _symbolStore.GetSymbolAsync(Repo, Sha, SymbolId.From("T:MyApp.Foo"));

        card.Should().NotBeNull();
        card!.FilePath.Value.Should().Be("src/App/Foo.cs");
        card.Confidence.Should().Be(Confidence.High,
            "a symbol with a bound file came from full semantic extraction");
    }

    [Fact]
    public async Task ToSymbolCard_OverlaySymbolWithoutFile_IsDowngradedToLow()
    {
        // Simulate a syntactic-fallback symbol: a SymbolRecord with FileIntId=0,
        // injected directly into the overlay (the baseline builder would drop it).
        var overlay = _symbolStore.TryGetOverlay(Repo.Value, Ws.Value)!;

        var stableSid = overlay.InternStringInternal("sym_overlay_ghost");
        var fqnSid = overlay.InternStringInternal("T:MyApp.Ghost");
        var displaySid = overlay.InternStringInternal("Ghost");
        var tokensSid = overlay.InternStringInternal("ghost myapp");
        var rec = new SymbolRecord(
            symbolIntId: -1, stableIdStringId: stableSid, fqnStringId: fqnSid,
            displayNameStringId: displaySid, namespaceStringId: 0,
            containerIntId: 0, fileIntId: 0, projectIntId: 0,   // FileIntId = 0: the fallback sentinel
            kind: 1, accessibility: 7, flags: 0, spanStart: 1, spanEnd: 10,
            nameTokensStringId: tokensSid, signatureHash: 0);

        using (var batch = overlay.BeginBatch())
        {
            batch.UpsertSymbol(rec, ["ghost", "myapp"]);
            await batch.CommitAsync();
        }

        var card = await _overlayStore.GetOverlaySymbolAsync(Repo, Ws, SymbolId.From("T:MyApp.Ghost"));

        card.Should().NotBeNull();
        card!.FilePath.Value.Should().Be("unknown",
            "FileIntId=0 renders as the 'unknown' sentinel — the syntactic-fallback marker");
        card.Confidence.Should().Be(Confidence.Low,
            "callers must see degraded confidence on syntactic-fallback symbols, not the "
            + "default High that pre-fix masked the broken-compile origin of the data");
    }
}
