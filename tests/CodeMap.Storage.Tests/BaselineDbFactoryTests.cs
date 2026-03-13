namespace CodeMap.Storage.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public class BaselineDbFactoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private BaselineDbFactory CreateFactory() =>
        new(_tempDir, NullLogger<BaselineDbFactory>.Instance);

    private static readonly RepoId TestRepo = RepoId.From("test-repo");
    private static readonly CommitSha TestSha = CommitSha.From(new string('a', 40));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void OpenOrCreate_FirstOpen_CreatesDirectory()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, TestSha);
        Directory.Exists(Path.GetDirectoryName(factory.GetDbPath(TestRepo, TestSha))!)
            .Should().BeTrue();
    }

    [Fact]
    public void OpenOrCreate_FirstOpen_CreatesSchemaVersionTable()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, TestSha);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'";
        ((long)cmd.ExecuteScalar()!).Should().Be(1);
    }

    [Fact]
    public void OpenOrCreate_FirstOpen_SetsSchemaVersion1()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, TestSha);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        ((long)cmd.ExecuteScalar()!).Should().Be(1);
    }

    [Fact]
    public void OpenOrCreate_CalledTwice_DoesNotThrow()
    {
        var factory = CreateFactory();
        var act = () =>
        {
            using var c1 = factory.OpenOrCreate(TestRepo, TestSha);
            using var c2 = factory.OpenOrCreate(TestRepo, TestSha);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void OpenOrCreate_CreatesAllRequiredTables()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, TestSha);

        var tables = GetTableNames(conn);
        tables.Should().Contain("files");
        tables.Should().Contain("symbols");
        tables.Should().Contain("refs");
        tables.Should().Contain("facts");
        tables.Should().Contain("schema_version");
        tables.Should().Contain("repo_meta");
    }

    [Fact]
    public void OpenOrCreate_CreatesFtsTable()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, TestSha);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='symbols_fts'";
        ((long)cmd.ExecuteScalar()!).Should().Be(1);
    }

    [Fact]
    public void OpenOrCreate_EnablesWalMode()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, TestSha);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = (string)cmd.ExecuteScalar()!;
        mode.Should().Be("wal");
    }

    [Fact]
    public void OpenExisting_FileDoesNotExist_ReturnsNull()
    {
        var factory = CreateFactory();
        var conn = factory.OpenExisting(TestRepo, TestSha);
        conn.Should().BeNull();
    }

    [Fact]
    public void OpenExisting_AfterCreate_ReturnsConnection()
    {
        var factory = CreateFactory();
        using var _ = factory.OpenOrCreate(TestRepo, TestSha);

        using var conn = factory.OpenExisting(TestRepo, TestSha);
        conn.Should().NotBeNull();
        conn!.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public void GetDbPath_ContainsCommitSha()
    {
        var factory = CreateFactory();
        var path = factory.GetDbPath(TestRepo, TestSha);
        path.Should().Contain(TestSha.Value);
    }

    [Fact]
    public void GetDbPath_EndsWithDbExtension()
    {
        var factory = CreateFactory();
        var path = factory.GetDbPath(TestRepo, TestSha);
        path.Should().EndWith(".db");
    }

    [Fact]
    public void GetDbPath_SanitizesRepoIdColons()
    {
        var factory = CreateFactory();
        var repoWithColon = RepoId.From("org/repo:name");
        var path = factory.GetDbPath(repoWithColon, TestSha);
        // Extract just the repo segment (relative to baseDir)
        var repoSegment = Path.GetRelativePath(_tempDir, Path.GetDirectoryName(path)!);
        repoSegment.Should().NotContain(":");
        repoSegment.Should().NotContain("/");
    }

    private static List<string> GetTableNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','shadow') AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }
}
