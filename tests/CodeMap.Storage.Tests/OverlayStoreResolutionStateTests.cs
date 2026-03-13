namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class OverlayStoreResolutionStateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    private static readonly RepoId Repo = RepoId.From("overlay-resstate-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-resstate");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));

    private static readonly string SymA = "M:TestNs.A.Run";
    private static readonly string SymB = "M:TestNs.B.Process";

    public OverlayStoreResolutionStateTests()
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

    private static ExtractedReference MakeUnresolved(
        string from,
        string to,
        string filePath,
        string toName,
        string? containerHint = "_svc") =>
        new(
            FromSymbol: SymbolId.From(from),
            ToSymbol: SymbolId.From(to),
            Kind: RefKind.Call,
            FilePath: FilePath.From(filePath),
            LineStart: 5,
            LineEnd: 5,
            ResolutionState: ResolutionState.Unresolved,
            ToName: toName,
            ToContainerHint: containerHint);

    // ── ApplyDelta with unresolved refs ──────────────────────────────────────

    [Fact]
    public async Task ApplyDelta_UnresolvedRefs_InsertedCorrectly()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/A.cs", "aabb11223344ccdd");
        var symA = OverlayTestHelpers.MakeSymbol(SymA, "A.Run", "src/A.cs", SymbolKind.Method);
        var symB = OverlayTestHelpers.MakeSymbol(SymB, "B.Process", "src/A.cs", SymbolKind.Method);

        var unresolvedRef = MakeUnresolved(SymA, SymB, "src/A.cs", "Process", "_svc");

        var delta = new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [symA, symB],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [unresolvedRef],
            DeletedReferenceFiles: [],
            NewRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        var refs = await _store.GetOverlayReferencesAsync(Repo, Workspace, SymbolId.From(SymB), null, 10);
        refs.Should().HaveCount(1);
        refs[0].ResolutionState.Should().Be(ResolutionState.Unresolved);
        refs[0].ToName.Should().Be("Process");
        refs[0].ToContainerHint.Should().Be("_svc");
    }

    // ── Mixed resolution states ──────────────────────────────────────────────

    [Fact]
    public async Task QueryOverlayRefs_MixedStates_BothReturned()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/A.cs", "aabb11223344ccdd");
        var symA = OverlayTestHelpers.MakeSymbol(SymA, "A.Run", "src/A.cs", SymbolKind.Method);
        var symB = OverlayTestHelpers.MakeSymbol(SymB, "B.Process", "src/A.cs", SymbolKind.Method);

        var resolvedRef = OverlayTestHelpers.MakeRef(SymA, SymB, "src/A.cs");
        var unresolvedRef = MakeUnresolved(SymA, SymB, "src/A.cs", "Execute", "_obj");

        var delta = new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [symA, symB],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [resolvedRef, unresolvedRef],
            DeletedReferenceFiles: [],
            NewRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        var refs = await _store.GetOverlayReferencesAsync(Repo, Workspace, SymbolId.From(SymB), null, 10);
        refs.Should().HaveCount(2);
        refs.Should().Contain(r => r.ResolutionState == ResolutionState.Resolved);
        refs.Should().Contain(r => r.ResolutionState == ResolutionState.Unresolved);
    }

    // ── Outgoing refs include resolution state ───────────────────────────────

    [Fact]
    public async Task QueryOverlayRefs_OutgoingRefs_IncludeResolutionState()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/A.cs", "aabb11223344ccdd");
        var symA = OverlayTestHelpers.MakeSymbol(SymA, "A.Run", "src/A.cs", SymbolKind.Method);
        var symB = OverlayTestHelpers.MakeSymbol(SymB, "B.Process", "src/A.cs", SymbolKind.Method);

        // One resolved + one unresolved outgoing from SymA
        var resolvedRef = OverlayTestHelpers.MakeRef(SymA, SymB, "src/A.cs");
        var unresolvedRef = new ExtractedReference(
            FromSymbol: SymbolId.From(SymA),
            ToSymbol: SymbolId.Empty,
            Kind: RefKind.Call,
            FilePath: FilePath.From("src/A.cs"),
            LineStart: 7,
            LineEnd: 7,
            ResolutionState: ResolutionState.Unresolved,
            ToName: "Unknown",
            ToContainerHint: "_x");

        var delta = new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [symA, symB],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [resolvedRef, unresolvedRef],
            DeletedReferenceFiles: [],
            NewRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        var outgoing = await _store.GetOutgoingOverlayReferencesAsync(
            Repo, Workspace, SymbolId.From(SymA), null, 10);

        outgoing.Should().HaveCount(2);
        outgoing.Should().Contain(r => r.ResolutionState == ResolutionState.Resolved);
        outgoing.Should().Contain(r => r.ResolutionState == ResolutionState.Unresolved);
        outgoing.Single(r => r.ResolutionState == ResolutionState.Unresolved)
            .ToName.Should().Be("Unknown");
    }
}
