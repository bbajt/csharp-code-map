namespace CodeMap.Storage.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class OverlayDbFactoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-001");

    public OverlayDbFactoryTests()
    {
        Directory.CreateDirectory(_tempDir);
        _factory = new OverlayDbFactory(_tempDir, NullLogger<OverlayDbFactory>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── GetDbPath ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetDbPath_ReturnsCorrectPath()
    {
        var path = _factory.GetDbPath(Repo, Workspace);

        path.Should().EndWith(Path.Combine("test-repo", "ws-001.db"));
    }

    [Fact]
    public void GetDbPath_SanitizesRepoId()
    {
        var dirtyRepo = RepoId.From("my.repo/name");
        var path = _factory.GetDbPath(dirtyRepo, Workspace);

        // The sanitized repo directory segment should not contain the original special chars.
        // Extract just the directory segment (i.e. "my_repo_name") between baseDir and filename.
        var relPath = Path.GetRelativePath(_tempDir, Path.GetDirectoryName(path)!);
        relPath.Should().Be("my_repo_name");
    }

    // ── OpenOrCreate ──────────────────────────────────────────────────────────

    [Fact]
    public void OpenOrCreate_CreatesDbFile()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);

        var path = _factory.GetDbPath(Repo, Workspace);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void OpenOrCreate_CreatesSchemaWithAllTables()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);

        var tables = GetTableNames(conn);
        tables.Should().Contain("overlay_meta");
        tables.Should().Contain("files");
        tables.Should().Contain("symbols");
        tables.Should().Contain("refs");
        tables.Should().Contain("deleted_symbols");
        tables.Should().Contain("schema_version");
    }

    [Fact]
    public void OpenOrCreate_IdempotentOnSecondCall()
    {
        using var conn1 = _factory.OpenOrCreate(Repo, Workspace);
        conn1.Dispose();

        // Should not throw — schema already exists
        using var conn2 = _factory.OpenOrCreate(Repo, Workspace);
        var tables = GetTableNames(conn2);
        tables.Should().Contain("overlay_meta");
    }

    [Fact]
    public void OpenOrCreate_SetsWalMode()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = (string)cmd.ExecuteScalar()!;
        mode.Should().Be("wal");
    }

    // ── OpenExisting ──────────────────────────────────────────────────────────

    [Fact]
    public void OpenExisting_ReturnsNullWhenNoFile()
    {
        var conn = _factory.OpenExisting(Repo, WorkspaceId.From("nonexistent"));

        conn.Should().BeNull();
    }

    [Fact]
    public void OpenExisting_ReturnsConnectionWhenFileExists()
    {
        using var _ = _factory.OpenOrCreate(Repo, Workspace);

        using var conn = _factory.OpenExisting(Repo, Workspace);
        conn.Should().NotBeNull();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesDbFile()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);
        conn.Dispose();

        SqliteConnection.ClearAllPools();
        _factory.Delete(Repo, Workspace);

        File.Exists(_factory.GetDbPath(Repo, Workspace)).Should().BeFalse();
    }

    [Fact]
    public void Delete_NoOpWhenFileDoesNotExist()
    {
        // Should not throw when file doesn't exist
        var act = () => _factory.Delete(Repo, WorkspaceId.From("ghost-ws"));
        act.Should().NotThrow();
    }

    // ── Schema validation ─────────────────────────────────────────────────────

    [Fact]
    public void Schema_HasOverlayMetaTable()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);
        GetTableNames(conn).Should().Contain("overlay_meta");
    }

    [Fact]
    public void Schema_HasDeletedSymbolsTable()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);
        GetTableNames(conn).Should().Contain("deleted_symbols");
    }

    [Fact]
    public void Schema_SymbolsHasContentHashColumn()
    {
        using var conn = _factory.OpenOrCreate(Repo, Workspace);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(symbols)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1)); // column name is index 1

        columns.Should().Contain("content_hash");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> GetTableNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }
}
