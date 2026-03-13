namespace CodeMap.Storage.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class BaselineCacheManagerTests : IDisposable
{
    private readonly string _localDir;
    private readonly string _cacheDir;
    private readonly BaselineDbFactory _localFactory;

    private static readonly RepoId TestRepo = RepoId.From("test-repo");
    private static readonly CommitSha TestSha = CommitSha.From(new string('a', 40));

    public BaselineCacheManagerTests()
    {
        _localDir = Path.Combine(Path.GetTempPath(), "bcm-local-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(Path.GetTempPath(), "bcm-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        Directory.CreateDirectory(_cacheDir);
        _localFactory = new BaselineDbFactory(_localDir, NullLogger<BaselineDbFactory>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_localDir)) try { Directory.Delete(_localDir, true); } catch { /* best-effort */ }
        if (Directory.Exists(_cacheDir)) try { Directory.Delete(_cacheDir, true); } catch { /* best-effort */ }
    }

    private BaselineCacheManager Make(bool cacheEnabled = true)
        => new(_localFactory, cacheEnabled ? _cacheDir : null, NullLogger<BaselineCacheManager>.Instance);

    /// Creates a valid SQLite DB in the local directory and returns its path.
    private string CreateLocalDb()
    {
        using (var conn = _localFactory.OpenOrCreate(TestRepo, TestSha)) { }
        // Clear pools so no pooled connections hold file handles before push/copy operations
        SqliteConnection.ClearAllPools();
        return _localFactory.GetDbPath(TestRepo, TestSha);
    }

    /// Returns the expected cache path for TestRepo/TestSha.
    private string CachePath() =>
        Path.Combine(_cacheDir, "test-repo", $"{TestSha.Value}.db");

    /// Creates a valid SQLite DB copy in the cache directory.
    private void CreateValidCacheDb()
    {
        var localPath = CreateLocalDb();
        var dir = Path.Combine(_cacheDir, "test-repo");
        Directory.CreateDirectory(dir);
        File.Copy(localPath, CachePath(), overwrite: true);
    }

    // ── ExistsInCacheAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ExistsInCache_CacheDisabled_ReturnsFalse()
    {
        var mgr = Make(cacheEnabled: false);
        var result = await mgr.ExistsInCacheAsync(TestRepo, TestSha);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsInCache_FileExists_ReturnsTrue()
    {
        CreateValidCacheDb();
        var mgr = Make();
        var result = await mgr.ExistsInCacheAsync(TestRepo, TestSha);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsInCache_FileNotExists_ReturnsFalse()
    {
        var mgr = Make();
        var result = await mgr.ExistsInCacheAsync(TestRepo, TestSha);
        result.Should().BeFalse();
    }

    // ── PullAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_CacheDisabled_ReturnsNull()
    {
        var mgr = Make(cacheEnabled: false);
        var result = await mgr.PullAsync(TestRepo, TestSha);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Pull_FileNotExists_ReturnsNull()
    {
        var mgr = Make();
        var result = await mgr.PullAsync(TestRepo, TestSha);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Pull_FileExists_CopiesToLocal()
    {
        CreateValidCacheDb();
        var mgr = Make();

        var result = await mgr.PullAsync(TestRepo, TestSha);

        var localPath = _localFactory.GetDbPath(TestRepo, TestSha);
        result.Should().Be(localPath);
        File.Exists(localPath).Should().BeTrue();

        // Content should be valid SQLite
        using var conn = new SqliteConnection($"Data Source={localPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        cmd.ExecuteScalar().Should().NotBeNull();
    }

    [Fact]
    public async Task Pull_AlreadyLocal_ReturnsLocalPath()
    {
        CreateValidCacheDb();
        var localPath = CreateLocalDb(); // already exists locally

        var mgr = Make();
        var result = await mgr.PullAsync(TestRepo, TestSha);

        result.Should().Be(localPath);
    }

    [Fact]
    public async Task Pull_CorruptCacheFile_ReturnsNull()
    {
        // Place 0-byte (corrupt) file in cache
        var dir = Path.Combine(_cacheDir, "test-repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(CachePath(), ""); // not a valid SQLite DB

        var mgr = Make();
        var result = await mgr.PullAsync(TestRepo, TestSha);

        result.Should().BeNull("corrupt file should be rejected");
        // Local file should not have been created
        File.Exists(_localFactory.GetDbPath(TestRepo, TestSha)).Should().BeFalse();
    }

    // ── PushAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_CacheDisabled_NoOp()
    {
        CreateLocalDb();
        var mgr = Make(cacheEnabled: false);

        await mgr.PushAsync(TestRepo, TestSha);

        File.Exists(CachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task Push_LocalNotExists_NoOp()
    {
        var mgr = Make();
        await mgr.PushAsync(TestRepo, TestSha);
        File.Exists(CachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task Push_LocalExists_CopiesToCache()
    {
        CreateLocalDb();
        var mgr = Make();

        await mgr.PushAsync(TestRepo, TestSha);

        File.Exists(CachePath()).Should().BeTrue();

        // Pushed file should be valid SQLite
        using var conn = new SqliteConnection($"Data Source={CachePath()}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        cmd.ExecuteScalar().Should().NotBeNull();
    }

    [Fact]
    public async Task Push_AlreadyCached_NoOp()
    {
        // Create a valid local DB and copy it to cache to simulate a prior push
        CreateLocalDb();
        var cacheSubDir = Path.Combine(_cacheDir, "test-repo");
        Directory.CreateDirectory(cacheSubDir);
        File.Copy(_localFactory.GetDbPath(TestRepo, TestSha), CachePath());
        var beforeModifiedUtc = File.GetLastWriteTimeUtc(CachePath());

        // Small delay so file modification time would differ if re-written
        await Task.Delay(20);

        var mgr = Make();
        await mgr.PushAsync(TestRepo, TestSha);

        // Cache file should be untouched (valid DB already present → skip)
        File.GetLastWriteTimeUtc(CachePath()).Should().Be(beforeModifiedUtc,
            because: "valid cached file should not be re-pushed");
    }

}
