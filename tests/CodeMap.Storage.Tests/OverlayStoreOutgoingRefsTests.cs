namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class OverlayStoreOutgoingRefsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    private static readonly RepoId Repo = RepoId.From("overlay-out-refs-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-out-refs");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));

    private static readonly string CallerSym = "M:MyNs.CallerA.Run";
    private static readonly string CalleeSym = "M:MyNs.ServiceB.Process";

    public OverlayStoreOutgoingRefsTests()
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

    private async Task SeedWithOutgoingRefsAsync()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/CallerA.cs", "aabb11223344ccdd");
        var callerSymbol = OverlayTestHelpers.MakeSymbol(CallerSym, "CallerA.Run", "src/CallerA.cs", SymbolKind.Method);
        var calleeSymbol = OverlayTestHelpers.MakeSymbol(CalleeSym, "ServiceB.Process", "src/CallerA.cs", SymbolKind.Method);
        var callRef = new ExtractedReference(
            FromSymbol: SymbolId.From(CallerSym),
            ToSymbol: SymbolId.From(CalleeSym),
            Kind: RefKind.Call,
            FilePath: FilePath.From("src/CallerA.cs"),
            LineStart: 7,
            LineEnd: 7);

        var delta = new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [callerSymbol, calleeSymbol],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [callRef],
            DeletedReferenceFiles: [],
            NewRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);
    }

    [Fact]
    public async Task GetOutgoing_OverlayHasOutgoingRefs_ReturnsCorrectly()
    {
        await SeedWithOutgoingRefsAsync();

        var refs = await _store.GetOutgoingOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From(CallerSym), null, 50);

        refs.Should().HaveCount(1);
        refs[0].ToSymbol.Value.Should().Be(CalleeSym);
        refs[0].Kind.Should().Be(RefKind.Call);
    }

    [Fact]
    public async Task GetOutgoing_EmptyOverlay_ReturnsEmpty()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var refs = await _store.GetOutgoingOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From(CallerSym), null, 50);

        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOutgoing_WithKindFilter_Filters()
    {
        await SeedWithOutgoingRefsAsync();

        var noHit = await _store.GetOutgoingOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From(CallerSym), RefKind.Write, 50);
        noHit.Should().BeEmpty();

        var hit = await _store.GetOutgoingOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From(CallerSym), RefKind.Call, 50);
        hit.Should().HaveCount(1);
    }
}
