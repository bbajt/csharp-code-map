namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;

public class BaselineStoreWriteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BaselineStore _store;

    public BaselineStoreWriteTests()
        => (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly Core.Types.RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly Core.Types.CommitSha Sha = StorageTestHelpers.TestSha;

    // ── BaselineExists ──────────────────────────────────────────────────────

    [Fact]
    public async Task BaselineExists_NoDb_ReturnsFalse()
    {
        var result = await _store.BaselineExistsAsync(Repo, Sha);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task BaselineExists_EmptyDb_ReturnsTrue()
    {
        // An indexed project with no symbols is still a valid baseline (e.g. empty project)
        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult());
        var result = await _store.BaselineExistsAsync(Repo, Sha);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task BaselineExists_AfterCreateWithSymbols_ReturnsTrue()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var symbol = StorageTestHelpers.MakeSymbol("Foo", "Foo", SymbolKind.Class, "src/Foo.cs");
        var data = StorageTestHelpers.MakeResult([symbol], [], [file]);

        await _store.CreateBaselineAsync(Repo, Sha, data);

        (await _store.BaselineExistsAsync(Repo, Sha)).Should().BeTrue();
    }

    // ── CreateBaseline ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBaseline_EmptyData_Succeeds()
    {
        var act = async () => await _store.CreateBaselineAsync(
            Repo, Sha, StorageTestHelpers.MakeResult());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateBaseline_WithFiles_InsertsAllFiles()
    {
        var files = new[]
        {
            StorageTestHelpers.MakeFile("src/A.cs", "aabbccdd11223344"),
            StorageTestHelpers.MakeFile("src/B.cs", "aabbccdd55667788"),
        };
        await _store.CreateBaselineAsync(Repo, Sha, StorageTestHelpers.MakeResult(files: files));

        var count = QueryScalar("SELECT COUNT(*) FROM files");
        count.Should().Be(2);
    }

    [Fact]
    public async Task CreateBaseline_WithSymbols_InsertsAllSymbols()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var symbols = new[]
        {
            StorageTestHelpers.MakeSymbol("Foo.A", "Foo.A", SymbolKind.Class,  "src/Foo.cs"),
            StorageTestHelpers.MakeSymbol("Foo.B", "Foo.B", SymbolKind.Method, "src/Foo.cs"),
        };
        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult(symbols, [], [file]));

        var count = QueryScalar("SELECT COUNT(*) FROM symbols");
        count.Should().Be(2);
    }

    [Fact]
    public async Task CreateBaseline_WithRefs_InsertsAllRefs()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var sym1 = StorageTestHelpers.MakeSymbol("Foo.A", "Foo.A", SymbolKind.Class, "src/Foo.cs");
        var sym2 = StorageTestHelpers.MakeSymbol("Foo.B", "Foo.B", SymbolKind.Method, "src/Foo.cs");
        var refRow = StorageTestHelpers.MakeRef("Foo.A", "Foo.B", RefKind.Call, "src/Foo.cs");
        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult([sym1, sym2], [refRow], [file]));

        var count = QueryScalar("SELECT COUNT(*) FROM refs");
        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateBaseline_AllSymbolKinds_StoredCorrectly()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var kinds = Enum.GetValues<SymbolKind>();
        var symbols = kinds.Select((k, i) =>
            StorageTestHelpers.MakeSymbol($"S{i}", $"S{i}", k, "src/Foo.cs")).ToArray();

        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult(symbols, [], [file]));

        var count = QueryScalar("SELECT COUNT(*) FROM symbols");
        count.Should().Be(kinds.Length);
    }

    [Fact]
    public async Task CreateBaseline_AllRefKinds_StoredCorrectly()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var sym1 = StorageTestHelpers.MakeSymbol("S1", "S1", SymbolKind.Method, "src/Foo.cs");
        var sym2 = StorageTestHelpers.MakeSymbol("S2", "S2", SymbolKind.Method, "src/Foo.cs");
        var refKinds = Enum.GetValues<RefKind>();
        var refs = refKinds.Select(k =>
            StorageTestHelpers.MakeRef("S1", "S2", k, "src/Foo.cs")).ToArray();

        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult([sym1, sym2], refs, [file]));

        var count = QueryScalar("SELECT COUNT(*) FROM refs");
        count.Should().Be(refKinds.Length);
    }

    [Fact]
    public async Task CreateBaseline_DuplicateSymbolId_LastWins()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var sym1 = StorageTestHelpers.MakeSymbol("Foo", "Foo", SymbolKind.Class, "src/Foo.cs", spanStart: 1);
        var sym2 = StorageTestHelpers.MakeSymbol("Foo", "Foo", SymbolKind.Struct, "src/Foo.cs", spanStart: 99);

        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult([sym1, sym2], [], [file]));

        var count = QueryScalar("SELECT COUNT(*) FROM symbols WHERE symbol_id = 'Foo'");
        count.Should().Be(1);

        var spanStart = QueryScalar("SELECT span_start FROM symbols WHERE symbol_id = 'Foo'");
        spanStart.Should().Be(99L); // last wins (INSERT OR REPLACE)
    }

    [Fact]
    public async Task CreateBaseline_NullOptionalFields_StoredAsNull()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var symbol = StorageTestHelpers.MakeSymbol("Foo", "Foo", SymbolKind.Class, "src/Foo.cs",
            documentation: null, containingType: null);

        await _store.CreateBaselineAsync(Repo, Sha,
            StorageTestHelpers.MakeResult([symbol], [], [file]));

        var doc = QueryScalarNullable("SELECT documentation FROM symbols WHERE symbol_id = 'Foo'");
        var ct = QueryScalarNullable("SELECT containing_type FROM symbols WHERE symbol_id = 'Foo'");
        doc.Should().BeNull();
        ct.Should().BeNull();
    }

    [Fact]
    public async Task CreateBaseline_CalledTwice_IsIdempotent()
    {
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var symbol = StorageTestHelpers.MakeSymbol("Foo", "Foo", SymbolKind.Class, "src/Foo.cs");
        var data = StorageTestHelpers.MakeResult([symbol], [], [file]);

        await _store.CreateBaselineAsync(Repo, Sha, data);
        var act = async () => await _store.CreateBaselineAsync(Repo, Sha, data);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateBaseline_CancellationRequested_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var symbol = StorageTestHelpers.MakeSymbol("Foo", "Foo", SymbolKind.Class, "src/Foo.cs");
        var data = StorageTestHelpers.MakeResult([symbol], [], [file]);

        var act = async () => await _store.CreateBaselineAsync(Repo, Sha, data, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private long QueryScalar(string sql)
    {
        var (_, dir) = StorageTestHelpers.CreateDiskStore();
        // Use the actual DB file created by the store
        var path = Path.Combine(_tempDir,
            SanitizeSegment(Repo.Value),
            $"{Sha.Value}.db");

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private object? QueryScalarNullable(string sql)
    {
        var path = Path.Combine(_tempDir,
            SanitizeSegment(Repo.Value),
            $"{Sha.Value}.db");

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == DBNull.Value ? null : result;
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
}
