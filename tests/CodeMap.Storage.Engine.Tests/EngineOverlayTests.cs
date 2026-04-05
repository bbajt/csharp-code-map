namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

public sealed class EngineOverlayTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-overlay-test-{Guid.NewGuid():N}");
    private EngineBaselineReader _reader = null!;
    private string _overlayDir = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(storeDir);

        var input = TestData.CreateTestInput();
        var result = await builder.BuildAsync(input, CancellationToken.None);
        result.Success.Should().BeTrue();

        _reader = new EngineBaselineReader(result.BaselinePath);
        _reader.InitSearch(new SearchIndexReader(_reader, Path.Combine(result.BaselinePath, "search.idx")));
        _reader.InitAdjacency(new AdjacencyIndexReader(
            Path.Combine(result.BaselinePath, "adjacency-out.idx"),
            Path.Combine(result.BaselinePath, "adjacency-in.idx"),
            _reader.SymbolCount));
        _overlayDir = Path.Combine(storeDir, "overlays", "test-ws");
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Create_WritesManifest()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        File.Exists(Path.Combine(_overlayDir, "manifest.json")).Should().BeTrue();
        overlay.Revision.Should().Be(0);
        overlay.NBaselineStringIds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UpsertSymbol_ThenQuery_ReturnsIt()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var stableIdSid = overlay.InternStringInternal("sym_overlay_0001");
        var fqnSid = overlay.InternStringInternal("T:MyApp.NewClass");
        var displaySid = overlay.InternStringInternal("NewClass");
        var tokensSid = overlay.InternStringInternal("newclass new class");

        var sym = new SymbolRecord(-1, stableIdSid, fqnSid, displaySid, 0, 0, 0, 0, 1, 7, 0, 1, 10, tokensSid, 0);

        using var batch = overlay.BeginBatch();
        batch.UpsertSymbol(sym, ["newclass", "new", "class"]);
        await batch.CommitAsync();

        overlay.Revision.Should().Be(1);
        var found = overlay.TryGetOverlaySymbol("sym_overlay_0001", out var tombstoned);
        found.Should().NotBeNull();
        tombstoned.Should().BeFalse();
        found!.Value.SymbolIntId.Should().Be(-1);
    }

    [Fact]
    public async Task Tombstone_HidesSymbol()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);

        // Get a baseline symbol's StableId
        var baselineSym = _reader.GetSymbolByFqn("T:MyApp.Foo");
        baselineSym.Should().NotBeNull();
        var stableId = _reader.ResolveString(baselineSym!.Value.StableIdStringId);

        using var batch = overlay.BeginBatch();
        batch.Tombstone(0, baselineSym.Value.SymbolIntId, stableId);
        await batch.CommitAsync();

        overlay.TryGetOverlaySymbol(stableId, out var tombstoned);
        tombstoned.Should().BeTrue();
        overlay.Tombstones.Should().Contain(stableId);
    }

    [Fact]
    public async Task AddEdge_VisibleInOutgoing()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var edge = new EdgeRecord(-1, 1, 2, 0, 1, 100, 110, 1, 0, 0, 1);

        using var batch = overlay.BeginBatch();
        batch.AddEdge(edge);
        await batch.CommitAsync();

        overlay.GetOverlayOutgoingEdges(1).Count.Should().Be(1);
    }

    [Fact]
    public void BatchDispose_WithoutCommit_NoChanges()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var stableIdSid = overlay.InternStringInternal("sym_discarded");
        var sym = new SymbolRecord(-1, stableIdSid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);

        using (var batch = overlay.BeginBatch())
        {
            batch.UpsertSymbol(sym, []);
            // Dispose without commit
        }

        overlay.Revision.Should().Be(0);
    }

    [Fact]
    public void InternString_ReturnsOverlayIds()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var id = overlay.InternStringInternal("overlay_string");
        id.Should().BeGreaterThan(overlay.NBaselineStringIds);
        overlay.ResolveString(id).Should().Be("overlay_string");
    }

    [Fact]
    public async Task Reopen_RecoverFromWal()
    {
        // Write some data, close, reopen — should recover
        {
            using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
            var stableIdSid = overlay.InternStringInternal("sym_persisted");
            var fqnSid = overlay.InternStringInternal("T:Persisted");
            var sym = new SymbolRecord(-1, stableIdSid, fqnSid, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);

            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();
        }

        // Reopen
        using var overlay2 = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var found = overlay2.TryGetOverlaySymbol("sym_persisted", out _);
        found.Should().NotBeNull();
    }

    [Fact]
    public async Task Checkpoint_ThenReopen_StatePreserved()
    {
        {
            using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
            var stableIdSid = overlay.InternStringInternal("sym_checkpoint");
            var sym = new SymbolRecord(-2, stableIdSid, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0);

            using var batch = overlay.BeginBatch();
            batch.UpsertSymbol(sym, []);
            await batch.CommitAsync();

            await overlay.DoCheckpointAsync();
        }

        File.Exists(Path.Combine(_overlayDir, "overlay.snapshot")).Should().BeTrue();

        using var overlay2 = new EngineOverlay(_overlayDir, "test-ws", _reader);
        overlay2.TryGetOverlaySymbol("sym_checkpoint", out _).Should().NotBeNull();
    }

    [Fact]
    public async Task MergedReader_TombstoneExcludesBaseline()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var baselineSym = _reader.GetSymbolByFqn("T:MyApp.Foo")!;
        var stableId = _reader.ResolveString(baselineSym.Value.StableIdStringId);

        using var batch = overlay.BeginBatch();
        batch.Tombstone(0, baselineSym.Value.SymbolIntId, stableId);
        await batch.CommitAsync();

        var merged = new EngineMergedReader(_reader, overlay);
        merged.GetSymbolByStableId(stableId).Should().BeNull();
        merged.GetSymbolByFqn("T:MyApp.Foo").Should().BeNull();
    }

    [Fact]
    public async Task MergedReader_OverlayEdgesMergedWithBaseline()
    {
        using var overlay = new EngineOverlay(_overlayDir, "test-ws", _reader);
        var doWork = _reader.GetSymbolByFqn("M:MyApp.Foo.DoWork")!;

        var overlayEdge = new EdgeRecord(-1, doWork.Value.SymbolIntId, 3, 0, 1, 200, 210, 1, 0, 0, 1);
        using var batch = overlay.BeginBatch();
        batch.AddEdge(overlayEdge);
        await batch.CommitAsync();

        var merged = new EngineMergedReader(_reader, overlay);
        var edges = merged.GetOutgoingEdges(doWork.Value.SymbolIntId);
        edges.Count.Should().BeGreaterThan(1); // baseline + overlay
    }
}

/// <summary>Shared test data factory.</summary>
internal static class TestData
{
    public static BaselineBuildInput CreateTestInput()
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/App/Foo.cs"), "aa" + new string('0', 62), "MyApp", "public class Foo { public void DoWork() { } }"),
            new("f2", FilePath.From("src/App/Bar.cs"), "bb" + new string('0', 62), "MyApp", "public class Bar { public int Process(string x) { return 0; } }"),
            new("f3", FilePath.From("src/App/IService.cs"), "cc" + new string('0', 62), "MyApp", "public interface IService { void Run(); }"),
        };

        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Foo"), "global::MyApp.Foo", SymbolKind.Class,
                "public class Foo", "MyApp", FilePath.From("src/App/Foo.cs"), 1, 10, "public", Confidence.High),
            SymbolCard.CreateMinimal(SymbolId.From("M:MyApp.Foo.DoWork"), "global::MyApp.Foo.DoWork", SymbolKind.Method,
                "public void DoWork()", "MyApp", FilePath.From("src/App/Foo.cs"), 3, 8, "public", Confidence.High, containingType: "Foo"),
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Bar"), "global::MyApp.Bar", SymbolKind.Class,
                "public class Bar", "MyApp", FilePath.From("src/App/Bar.cs"), 1, 5, "public", Confidence.High),
            SymbolCard.CreateMinimal(SymbolId.From("M:MyApp.Bar.Process"), "global::MyApp.Bar.Process", SymbolKind.Method,
                "public int Process(string x)", "MyApp", FilePath.From("src/App/Bar.cs"), 2, 4, "internal", Confidence.High, containingType: "Bar"),
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.IService"), "global::MyApp.IService", SymbolKind.Interface,
                "public interface IService", "MyApp", FilePath.From("src/App/IService.cs"), 1, 3, "public", Confidence.High),
        };

        var refs = new List<ExtractedReference>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), SymbolId.From("M:MyApp.Bar.Process"), RefKind.Call, FilePath.From("src/App/Foo.cs"), 5, 5),
            new(SymbolId.From("T:MyApp.Foo"), SymbolId.From("T:MyApp.IService"), RefKind.Implementation, FilePath.From("src/App/Foo.cs"), 1, 1),
        };

        var facts = new List<ExtractedFact>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), null, FactKind.Route, "GET|/api/foo", FilePath.From("src/App/Foo.cs"), 4, 4, Confidence.High),
        };

        return new BaselineBuildInput("abcdef0123456789abcdef0123456789abcdef01", @"C:\repo", symbols, files, refs, facts, []);
    }
}
