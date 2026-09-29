namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-04 T01 regression for F4: on Unix, .NET emulates <see cref="FileShare"/> with advisory
/// <c>flock</c>, and every share mode except <see cref="FileShare.None"/> takes a <em>shared</em> lock.
/// <c>WalWriter</c> opens <c>overlay.wal</c> with <see cref="FileShare.Read"/>, so a second process
/// opening the same overlay replayed the WAL, opened its own writer and appended alongside the
/// first — interleaving (and, on checkpoint truncation, losing) records without any error.
/// Windows already refused the second writer (F3). An exclusive per-overlay
/// <c>overlay.lock</c> now makes the second open fail on every OS (ADR-044).
/// Each <see cref="CustomSymbolStore"/> instance stands in for one daemon process.
/// </summary>
public sealed class OverlayWriterExclusionTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-ovl-excl-{Guid.NewGuid():N}");
    private readonly List<CustomSymbolStore> _stores = [];
    private string _storeBaseDir = null!;

    private static readonly RepoId Repo = RepoId.From("repo-a");
    private static readonly WorkspaceId Session = WorkspaceId.From("session");
    private static readonly WorkspaceId Other = WorkspaceId.From("other");
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");

    public async ValueTask InitializeAsync()
    {
        _storeBaseDir = Path.Combine(_tempDir, "store");
        var built = await new EngineBaselineBuilder(Path.Combine(_storeBaseDir, Repo.Value))
            .BuildAsync(TestData.CreateTestInput(), CancellationToken.None);
        built.Success.Should().BeTrue();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var s in _stores) s.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SameWorkspace_TwoStoreInstances_SecondCreateThrowsIOException()
    {
        var (_, first) = NewProcess();
        var (_, second) = NewProcess();
        await first.CreateOverlayAsync(Repo, Session, Sha);

        var act = async () => await second.CreateOverlayAsync(Repo, Session, Sha);

        await act.Should().ThrowAsync<IOException>(
            "a second process must never open an overlay another process is writing (F4)");
    }

    [Fact]
    public async Task SameWorkspace_SecondWriter_IsRejectedNotSilentlyLost()
    {
        var (firstStore, first) = NewProcess();
        var (secondStore, second) = NewProcess();
        await first.CreateOverlayAsync(Repo, Session, Sha);
        await first.ApplyDeltaAsync(Repo, Session, AddSymbolDelta("T:MyApp.Zed", 1));

        // Pre-fix on Unix both calls succeed: the second process appends to the same WAL and
        // believes its edit is stored.
        var secondWriteAccepted = false;
        try
        {
            await second.CreateOverlayAsync(Repo, Session, Sha);
            await second.ApplyDeltaAsync(Repo, Session, AddSymbolDelta("T:MyApp.Intruder", 2));
            secondWriteAccepted = true;
        }
        catch (IOException) { /* expected: the overlay is held by the first process */ }

        await first.ApplyDeltaAsync(Repo, Session, AddSymbolDelta("T:MyApp.Yod", 2));
        firstStore.Dispose(); // graceful checkpoint: snapshot from memory + WAL truncation
        secondStore.Dispose();

        var (_, reopened) = NewProcess();
        await reopened.CreateOverlayAsync(Repo, Session, Sha);
        var names = await Names(reopened);

        names.Should().Contain(["T:MyApp.Zed", "T:MyApp.Yod"], "the owning writer's edits survive");
        if (secondWriteAccepted)
            names.Should().Contain("T:MyApp.Intruder",
                "an edit the store accepted without error must not be lost silently (F4)");
        else
            names.Should().NotContain("T:MyApp.Intruder");
    }

    [Fact]
    public async Task SameWorkspace_FirstDisposed_SecondCreateSucceeds()
    {
        var (firstStore, first) = NewProcess();
        await first.CreateOverlayAsync(Repo, Session, Sha);
        await first.ApplyDeltaAsync(Repo, Session, AddSymbolDelta("T:MyApp.Zed", 1));
        firstStore.Dispose(); // process exit releases the lock

        var (_, second) = NewProcess();
        await second.CreateOverlayAsync(Repo, Session, Sha);

        (await Names(second)).Should().BeEquivalentTo(["T:MyApp.Zed"]);
    }

    [Fact]
    public async Task WriterLockFile_HeldExclusivelyWhileOpen()
    {
        var (_, first) = NewProcess();
        await first.CreateOverlayAsync(Repo, Session, Sha);

        var lockPath = Path.Combine(_storeBaseDir, Repo.Value, "overlays", Session.Value, "overlay.lock");
        File.Exists(lockPath).Should().BeTrue("the overlay writer lock is documented in STORAGE-FORMAT.MD");

        var open = () => new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
        open.Should().Throw<IOException>("the lock is held with FileShare.None for the overlay's lifetime");
    }

    [Fact]
    public async Task DifferentWorkspaces_SameRepo_DoNotBlock()
    {
        var (_, first) = NewProcess();
        var (_, second) = NewProcess();
        await first.CreateOverlayAsync(Repo, Session, Sha);

        var act = async () => await second.CreateOverlayAsync(Repo, Other, Sha);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CommitKeyedOverlay_TwoInstances_SecondThrows()
    {
        var (firstStore, _) = NewProcess();
        var (secondStore, _) = NewProcess();
        await firstStore.InsertMetadataStubsAsync(Repo, Sha, [Stub("T:Ext.First")]);

        var act = async () => await secondStore.InsertMetadataStubsAsync(Repo, Sha, [Stub("T:Ext.Second")]);

        await act.Should().ThrowAsync<IOException>(
            "baseline-level overlays are written by whichever process runs resolution/decompilation");
    }

    [Fact]
    public async Task DeleteOverlay_RemovesLockFile_AndRecreateSucceeds()
    {
        var (_, first) = NewProcess();
        var (_, second) = NewProcess();
        await first.CreateOverlayAsync(Repo, Session, Sha);
        var overlayDir = Path.Combine(_storeBaseDir, Repo.Value, "overlays", Session.Value);
        File.Exists(Path.Combine(overlayDir, "overlay.lock")).Should().BeTrue();

        await first.DeleteOverlayAsync(Repo, Session);

        Directory.Exists(overlayDir).Should().BeFalse("deleting the workspace removes its directory, lock included");
        var act = async () => await second.CreateOverlayAsync(Repo, Session, Sha);
        await act.Should().NotThrowAsync();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (CustomSymbolStore Store, CustomEngineOverlayStore Overlays) NewProcess()
    {
        var store = new CustomSymbolStore(_storeBaseDir);
        _stores.Add(store);
        return (store, new CustomEngineOverlayStore(store, _storeBaseDir));
    }

    private static async Task<IReadOnlyList<string>> Names(CustomEngineOverlayStore overlays)
    {
        var hits = await overlays.GetOverlaySymbolsByKindsAsync(Repo, Session, [SymbolKind.Class], null, 100);
        return hits.Select(h => h.FullyQualifiedName).ToList();
    }

    private static SymbolCard Stub(string symbolId)
    {
        var simple = symbolId[(symbolId.LastIndexOf('.') + 1)..];
        var ns = symbolId[2..symbolId.LastIndexOf('.')];
        return SymbolCard.CreateMinimal(SymbolId.From(symbolId), $"global::{symbolId[2..]}", SymbolKind.Class,
            $"public class {simple}", ns, FilePath.From($"src/App/{simple}.cs"), 1, 5, "public", Confidence.High);
    }

    private static OverlayDelta AddSymbolDelta(string symbolId, int revision) => new(
        ReindexedFiles: [],
        AddedOrUpdatedSymbols: [Stub(symbolId)],
        DeletedSymbolIds: [],
        AddedOrUpdatedReferences: [],
        DeletedReferenceFiles: [],
        NewRevision: revision);
}
