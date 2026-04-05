namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

public sealed class SnapshotSerializerRoundtripTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-snap-test-{Guid.NewGuid():N}");
    private EngineBaselineReader _reader = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(storeDir);
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

    [Fact]
    public Task EmptyOverlay_RoundTrip()
    {
        var overlayDir = Path.Combine(_tempDir, "overlay-empty");
        using var overlay = new EngineOverlay(overlayDir, "ws-empty", _reader);

        var snapshotPath = Path.Combine(overlayDir, "overlay.snapshot");
        SnapshotSerializer.Write(snapshotPath, overlay);

        // Create a fresh overlay and load snapshot
        var overlayDir2 = Path.Combine(_tempDir, "overlay-empty2");
        Directory.CreateDirectory(overlayDir2);
        using var overlay2 = new EngineOverlay(overlayDir2, "ws-empty2", _reader);

        // Read into overlay2's state
        SnapshotSerializer.Read(snapshotPath, overlay2);

        overlay2.SymbolsByStableId.Should().BeEmpty();
        overlay2.TombstoneSet.Should().BeEmpty();
        overlay2.OutgoingEdges.Should().BeEmpty();
        overlay2.FactsBySymbol.Should().BeEmpty();
        overlay2.FilesByPath.Should().BeEmpty();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FullOverlay_RoundTrip()
    {
        var overlayDir = Path.Combine(_tempDir, "overlay-full");
        using var overlay = new EngineOverlay(overlayDir, "ws-full", _reader);

        // Add symbol
        var stableIdSid = overlay.InternStringInternal("sym_snap_test");
        var fqnSid = overlay.InternStringInternal("T:Snap.Test");
        var displaySid = overlay.InternStringInternal("Test");
        var sym = new SymbolRecord(-1, stableIdSid, fqnSid, displaySid, 0, 0, 0, 0, 1, 7, 0, 1, 10, 0, 0);

        using var batch = overlay.BeginBatch();
        batch.UpsertSymbol(sym, ["test", "snap"]);

        // Add edge
        var edge = new EdgeRecord(-1, 1, -1, 0, 1, 5, 10, 1, 0, 0, 1);
        batch.AddEdge(edge);

        // Add fact
        var primarySid = overlay.InternStringInternal("GET");
        var secondarySid = overlay.InternStringInternal("/api");
        var fact = new FactRecord(-1, -1, 1, 5, 5, 0, primarySid, secondarySid, 0, 0);
        batch.AddFact(fact);

        // Add tombstone
        var baselineSym = _reader.GetSymbolByFqn("T:MyApp.Foo");
        if (baselineSym != null)
        {
            var bsStableId = _reader.ResolveString(baselineSym.Value.StableIdStringId);
            batch.Tombstone(0, baselineSym.Value.SymbolIntId, bsStableId);
        }

        await batch.CommitAsync();

        // Snapshot
        var snapshotPath = Path.Combine(_tempDir, "test-snapshot.bin");
        SnapshotSerializer.Write(snapshotPath, overlay);

        // Verify by reading into a fresh overlay
        var overlayDir2 = Path.Combine(_tempDir, "overlay-verify");
        Directory.CreateDirectory(overlayDir2);
        using var overlay2 = new EngineOverlay(overlayDir2, "ws-verify", _reader);
        SnapshotSerializer.Read(snapshotPath, overlay2);

        // Verify all state
        overlay2.SymbolsByStableId.Should().ContainKey("sym_snap_test");
        overlay2.SymbolsByStableId["sym_snap_test"].SymbolIntId.Should().Be(-1);
        overlay2.TombstoneSet.Count.Should().Be(overlay.TombstoneSet.Count);
        overlay2.OutgoingEdges.Should().NotBeEmpty();
        overlay2.FactsBySymbol.Should().NotBeEmpty();
        overlay2.OverlayDictionary.Should().NotBeEmpty();
        overlay2.Revision.Should().Be(overlay.Revision);
        overlay2.NextOverlaySymbolIntId.Should().Be(overlay.NextOverlaySymbolIntId);
        overlay2.NextOverlayEdgeIntId.Should().Be(overlay.NextOverlayEdgeIntId);
        overlay2.NextOverlayFileIntId.Should().Be(overlay.NextOverlayFileIntId);
        overlay2.NextOverlayFactIntId.Should().Be(overlay.NextOverlayFactIntId);
    }

    [Fact]
    public async Task Counters_PreservedOnRoundTrip()
    {
        var overlayDir = Path.Combine(_tempDir, "overlay-counters");
        using var overlay = new EngineOverlay(overlayDir, "ws-ctr", _reader);

        // Perform several mutations to decrement counters
        for (var i = 0; i < 3; i++)
        {
            var sid = overlay.InternStringInternal($"sym_ctr_{i}");
            var sym = new SymbolRecord(overlay.NextOverlaySymbolIntId--, sid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);
            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();
        }

        var snapshotPath = Path.Combine(_tempDir, "counters.bin");
        SnapshotSerializer.Write(snapshotPath, overlay);

        var overlayDir2 = Path.Combine(_tempDir, "overlay-ctr2");
        Directory.CreateDirectory(overlayDir2);
        using var overlay2 = new EngineOverlay(overlayDir2, "ws-ctr2", _reader);
        SnapshotSerializer.Read(snapshotPath, overlay2);

        overlay2.NextOverlaySymbolIntId.Should().Be(overlay.NextOverlaySymbolIntId);
        overlay2.NextOverlayStringId.Should().Be(overlay.NextOverlayStringId);
    }
}
