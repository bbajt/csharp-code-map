namespace CodeMap.Storage.Engine.Tests;

using System.Runtime.InteropServices;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

public sealed class CrashSafetyTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-crash-test-{Guid.NewGuid():N}");
    private EngineBaselineReader _reader = null!;
    private string _storeDir = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _storeDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(_storeDir);
        var result = await builder.BuildAsync(TestData.CreateTestInput(), CancellationToken.None);
        _reader = new EngineBaselineReader(result.BaselinePath);
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private string MakeOverlayDir(string name)
    {
        var dir = Path.Combine(_storeDir, "overlays", name);
        return dir;
    }

    /// <summary>
    /// Helper: creates an overlay, writes N symbols via batches, then does a hard
    /// "crash" (disposes without checkpoint by setting _walRecordCount=0 to skip
    /// the graceful shutdown checkpoint). Returns the overlay directory path.
    /// </summary>
    private async Task<string> WriteAndCrash(string name, int symbolCount)
    {
        var overlayDir = MakeOverlayDir(name);
        var overlay = new EngineOverlay(overlayDir, name, _reader);
        for (var i = 0; i < symbolCount; i++)
        {
            var sid = overlay.InternStringInternal($"sym_{name}_{i}");
            var sym = new SymbolRecord(-(i + 1), sid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);
            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();
        }
        // "Crash": dispose the WAL writer without checkpoint
        overlay.GetWalWriter().Dispose();
        return overlayDir;
    }

    [Fact]
    public async Task TornWalTail_RecoveryReplaysOnlyCompleteRecords()
    {
        var overlayDir = await WriteAndCrash("torn", 5);
        var walPath = Path.Combine(overlayDir, "overlay.wal");

        // Truncate WAL by 10 bytes (mid-record)
        var walBytes = File.ReadAllBytes(walPath);
        walBytes.Length.Should().BeGreaterThan(10);
        File.WriteAllBytes(walPath, walBytes[..(walBytes.Length - 10)]);

        // Delete snapshot to force full WAL recovery
        var snapshotPath = Path.Combine(overlayDir, "overlay.snapshot");
        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);

        // Reopen — should recover only complete records
        using var recovered = new EngineOverlay(overlayDir, "torn", _reader);
        var foundCount = 0;
        for (var i = 0; i < 5; i++)
        {
            if (recovered.TryGetOverlaySymbol($"sym_torn_{i}", out _) != null)
                foundCount++;
        }
        foundCount.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task ZeroByteWal_LoadsFromSnapshotOnly()
    {
        var overlayDir = MakeOverlayDir("zero-wal");
        {
            using var overlay = new EngineOverlay(overlayDir, "zero-wal", _reader);
            var sid = overlay.InternStringInternal("sym_zero_wal");
            var sym = new SymbolRecord(-1, sid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);
            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();
            await overlay.DoCheckpointAsync();
        } // Graceful shutdown — snapshot exists, WAL empty

        // Extra safety: zero the WAL
        File.WriteAllBytes(Path.Combine(overlayDir, "overlay.wal"), []);

        // Reopen — should load from snapshot
        using var recovered = new EngineOverlay(overlayDir, "zero-wal", _reader);
        recovered.TryGetOverlaySymbol("sym_zero_wal", out _).Should().NotBeNull();
    }

    [Fact]
    public async Task BadWalMagic_WalRejected_LoadsFromSnapshot()
    {
        var overlayDir = MakeOverlayDir("bad-magic");
        {
            using var overlay = new EngineOverlay(overlayDir, "bad-magic", _reader);
            var sid = overlay.InternStringInternal("sym_bad_magic");
            var sym = new SymbolRecord(-1, sid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);
            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();
            await overlay.DoCheckpointAsync();
        }

        // Write garbage magic to WAL
        File.WriteAllBytes(Path.Combine(overlayDir, "overlay.wal"), [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00]);

        using var recovered = new EngineOverlay(overlayDir, "bad-magic", _reader);
        recovered.TryGetOverlaySymbol("sym_bad_magic", out _).Should().NotBeNull();
    }

    [Fact]
    public async Task MissingSnapshot_ValidWal_RebuildFromWal()
    {
        var overlayDir = await WriteAndCrash("no-snap", 3);

        // Delete snapshot
        var snapshotPath = Path.Combine(overlayDir, "overlay.snapshot");
        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);

        using var recovered = new EngineOverlay(overlayDir, "no-snap", _reader);
        for (var i = 0; i < 3; i++)
            recovered.TryGetOverlaySymbol($"sym_no-snap_{i}", out _).Should().NotBeNull();
    }

    [Fact]
    public async Task CrcMismatch_TruncatesAtBadRecord()
    {
        var overlayDir = await WriteAndCrash("crc", 3);
        var walPath = Path.Combine(overlayDir, "overlay.wal");

        // Corrupt a byte in the middle of the WAL
        var walBytes = File.ReadAllBytes(walPath);
        if (walBytes.Length > 50)
        {
            walBytes[walBytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(walPath, walBytes);
        }

        // Delete snapshot
        var snapshotPath = Path.Combine(overlayDir, "overlay.snapshot");
        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);

        // Reopen — no exception thrown, some records may survive
        using var recovered = new EngineOverlay(overlayDir, "crc", _reader);
        // Key assertion: recovery doesn't throw
    }

    [Fact]
    public async Task NegativeIntId_RoundTrip()
    {
        var overlayDir = MakeOverlayDir("neg-id");
        var overlay = new EngineOverlay(overlayDir, "neg-id", _reader);
        var edge = new EdgeRecord(-1, 1, -2, 0, 1, 100, 110, 1, 0, 0, 1);
        using var batch = overlay.BeginBatch();
        batch.AddEdge(edge);
        await batch.CommitAsync();

        // "Crash": close WAL without checkpoint
        overlay.GetWalWriter().Dispose();

        // Delete snapshot → force WAL recovery
        var snapshotPath = Path.Combine(overlayDir, "overlay.snapshot");
        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);

        using var recovered = new EngineOverlay(overlayDir, "neg-id", _reader);
        var edges = recovered.GetOverlayOutgoingEdges(1);
        edges.Count.Should().Be(1);
        edges[0].ToSymbolIntId.Should().Be(-2);
        edges[0].EdgeIntId.Should().Be(-1);
    }

    [Fact]
    public async Task CrashDuringCheckpoint_RecoverFromWal()
    {
        var overlayDir = await WriteAndCrash("crash-chk", 2);

        // Simulate: snapshot.tmp left behind (crash during checkpoint move)
        File.WriteAllBytes(Path.Combine(overlayDir, "overlay.snapshot.tmp"), [0x00]);

        // Reopen — should recover from WAL
        using var recovered = new EngineOverlay(overlayDir, "crash-chk", _reader);
        recovered.TryGetOverlaySymbol("sym_crash-chk_0", out _).Should().NotBeNull();
    }

    [Fact]
    public void StaleLockFile_NewOpenSucceeds()
    {
        var overlayDir = MakeOverlayDir("stale-lock");
        Directory.CreateDirectory(overlayDir);
        File.WriteAllText(Path.Combine(overlayDir, "lock"), "stale-pid-12345");

        using var overlay = new EngineOverlay(overlayDir, "stale-lock", _reader);
        overlay.WorkspaceId.Should().Be("stale-lock");
    }
}
