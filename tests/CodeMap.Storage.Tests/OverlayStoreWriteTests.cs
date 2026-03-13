namespace CodeMap.Storage.Tests;

using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class OverlayStoreWriteTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    private static readonly RepoId Repo = RepoId.From("write-test-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-write");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));

    public OverlayStoreWriteTests()
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

    // ── CreateOverlayAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Create_SetsRevisionToZero()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var rev = await _store.GetRevisionAsync(Repo, Workspace);
        rev.Should().Be(0);
    }

    [Fact]
    public async Task Create_StoresBaselineCommitSha()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        var sha = QueryMeta(conn, "baseline_commit_sha");
        sha.Should().Be(Sha.Value);
    }

    [Fact]
    public async Task Create_StoresWorkspaceId()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        var ws = QueryMeta(conn, "workspace_id");
        ws.Should().Be(Workspace.Value);
    }

    [Fact]
    public async Task Create_IdempotentOnSecondCall()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var act = async () => await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await act.Should().NotThrowAsync();
        (await _store.GetRevisionAsync(Repo, Workspace)).Should().Be(0);
    }

    // ── ApplyDeltaAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDelta_InsertsFiles()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta();

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "files").Should().Be(1);
    }

    [Fact]
    public async Task ApplyDelta_InsertsSymbols()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta();

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "symbols").Should().Be(1);
    }

    [Fact]
    public async Task ApplyDelta_InsertsRefs()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var file = OverlayTestHelpers.MakeFile("src/Bar.cs", "bbbbbbbbbbbbbbbb");
        var symbol = OverlayTestHelpers.MakeSymbol("M:TestNs.Bar.Run", "TestNs.Bar.Run", "src/Bar.cs");
        var refr = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/Bar.cs");
        var delta = OverlayTestHelpers.MakeDelta([file], [symbol], null, [refr]);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "refs").Should().Be(1);
    }

    [Fact]
    public async Task ApplyDelta_RecordsDeletedSymbols()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var deletedId = SymbolId.From("T:TestNs.OldClass");
        var delta = OverlayTestHelpers.MakeDelta(deletedIds: [deletedId]);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "deleted_symbols").Should().Be(1);
    }

    [Fact]
    public async Task ApplyDelta_IncrementsRevision()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta(newRevision: 1);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        (await _store.GetRevisionAsync(Repo, Workspace)).Should().Be(1);
    }

    [Fact]
    public async Task ApplyDelta_ReplacesOldSymbolsFromSameFile()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // First delta: 1 symbol
        var delta1 = OverlayTestHelpers.MakeDelta(newRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta1);

        // Second delta: 2 symbols in same file (should replace, not accumulate)
        var file = OverlayTestHelpers.MakeFile(); // same file_id
        var s1 = OverlayTestHelpers.MakeSymbol("T:TestNs.Foo");
        var s2 = OverlayTestHelpers.MakeSymbol("T:TestNs.Bar", "TestNs.Bar");
        var delta2 = OverlayTestHelpers.MakeDelta([file], [s1, s2], newRevision: 2);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta2);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "symbols").Should().Be(2);
    }

    [Fact]
    public async Task ApplyDelta_ReplacesOldRefsFromSameFile()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // File and symbol for the ref
        var file = OverlayTestHelpers.MakeFile("src/Bar.cs", "bbbbbbbbbbbbbbbb");
        var symbol = OverlayTestHelpers.MakeSymbol("M:TestNs.Bar.Run", "TestNs.Bar.Run", "src/Bar.cs");
        var refr1 = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/Bar.cs");
        var delta1 = OverlayTestHelpers.MakeDelta([file], [symbol], null, [refr1], newRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta1);

        // Second delta for same file — should replace refs
        var refr2 = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Baz", "src/Bar.cs");
        var delta2 = OverlayTestHelpers.MakeDelta([file], [symbol], null, [refr2], newRevision: 2);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta2);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "refs").Should().Be(1); // still 1, not 2
    }

    [Fact]
    public async Task ApplyDelta_ReAddedSymbol_RemovedFromDeletedList()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // Delete in revision 1
        var deletedId = SymbolId.From("T:TestNs.Foo");
        var delta1 = OverlayTestHelpers.MakeDelta(
            files: [OverlayTestHelpers.MakeFile()],
            symbols: [],
            deletedIds: [deletedId],
            newRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta1);

        // Re-add in revision 2
        var delta2 = OverlayTestHelpers.MakeDelta(newRevision: 2);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta2);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "deleted_symbols").Should().Be(0);
    }

    [Fact]
    public async Task ApplyDelta_SkipsSymbolsWithNoMatchingFile()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // Symbol references a file not in delta.ReindexedFiles
        var file = OverlayTestHelpers.MakeFile("src/Foo.cs");
        var symbol = OverlayTestHelpers.MakeSymbol("T:TestNs.Bar", "TestNs.Bar", "src/UnknownFile.cs");
        var delta = OverlayTestHelpers.MakeDelta([file], [symbol]);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "symbols").Should().Be(0);
    }

    [Fact]
    public async Task ApplyDelta_SkipsRefsWithNoMatchingFile()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var file = OverlayTestHelpers.MakeFile("src/Foo.cs");
        var refr = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/OtherFile.cs");
        var delta = OverlayTestHelpers.MakeDelta([file], [], null, [refr]);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "refs").Should().Be(0);
    }

    [Fact]
    public async Task ApplyDelta_RebuildsFtsAfterInsert()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta();

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        // FTS should return the symbol we just inserted
        var hits = await _store.SearchOverlaySymbolsAsync(
            Repo, Workspace, "Foo", null, 10);
        hits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ApplyDelta_MultipleDeltasAccumulate()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // Delta 1: file A with symbol A
        var fileA = OverlayTestHelpers.MakeFile("src/A.cs", "aaaaaaaaaaaaaaaa");
        var symbolA = OverlayTestHelpers.MakeSymbol("T:TestNs.A", "TestNs.A", "src/A.cs");
        var delta1 = OverlayTestHelpers.MakeDelta([fileA], [symbolA], newRevision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta1);

        // Delta 2: file B with symbol B (different file, should accumulate)
        var fileB = OverlayTestHelpers.MakeFile("src/B.cs", "bbbbbbbbbbbbbbbb");
        var symbolB = OverlayTestHelpers.MakeSymbol("T:TestNs.B", "TestNs.B", "src/B.cs");
        var delta2 = OverlayTestHelpers.MakeDelta([fileB], [symbolB], newRevision: 2);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta2);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "files").Should().Be(2);
        CountRows(conn, "symbols").Should().Be(2);
        (await _store.GetRevisionAsync(Repo, Workspace)).Should().Be(2);
    }

    // ── ResetOverlayAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_ClearsAllSymbols()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        await _store.ResetOverlayAsync(Repo, Workspace);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "symbols").Should().Be(0);
    }

    [Fact]
    public async Task Reset_ClearsAllRefs()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var file = OverlayTestHelpers.MakeFile("src/Bar.cs", "bbbbbbbbbbbbbbbb");
        var symbol = OverlayTestHelpers.MakeSymbol("M:TestNs.Bar.Run", "TestNs.Bar.Run", "src/Bar.cs");
        var refr = OverlayTestHelpers.MakeRef("M:TestNs.Bar.Run", "T:TestNs.Foo", "src/Bar.cs");
        await _store.ApplyDeltaAsync(Repo, Workspace,
            OverlayTestHelpers.MakeDelta([file], [symbol], null, [refr]));

        await _store.ResetOverlayAsync(Repo, Workspace);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "refs").Should().Be(0);
    }

    [Fact]
    public async Task Reset_ClearsAllFiles()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        await _store.ResetOverlayAsync(Repo, Workspace);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "files").Should().Be(0);
    }

    [Fact]
    public async Task Reset_ClearsDeletedSymbols()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var delta = OverlayTestHelpers.MakeDelta(deletedIds: [SymbolId.From("T:TestNs.Old")]);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        await _store.ResetOverlayAsync(Repo, Workspace);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        CountRows(conn, "deleted_symbols").Should().Be(0);
    }

    [Fact]
    public async Task Reset_ResetsRevisionToZero()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta(newRevision: 5));

        await _store.ResetOverlayAsync(Repo, Workspace);

        (await _store.GetRevisionAsync(Repo, Workspace)).Should().Be(0);
    }

    [Fact]
    public async Task Reset_PreservesMetadata()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        await _store.ApplyDeltaAsync(Repo, Workspace, OverlayTestHelpers.MakeDelta());

        await _store.ResetOverlayAsync(Repo, Workspace);

        using var conn = _factory.OpenExisting(Repo, Workspace)!;
        QueryMeta(conn, "baseline_commit_sha").Should().Be(Sha.Value);
        QueryMeta(conn, "workspace_id").Should().Be(Workspace.Value);
    }

    // ── DeleteOverlayAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesDbFile()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);
        var path = _factory.GetDbPath(Repo, Workspace);

        await _store.DeleteOverlayAsync(Repo, Workspace);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NoOpWhenNotExists()
    {
        var ghost = WorkspaceId.From("ghost-workspace");
        var act = async () => await _store.DeleteOverlayAsync(Repo, ghost);
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountRows(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static string? QueryMeta(Microsoft.Data.Sqlite.SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM overlay_meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }
}
