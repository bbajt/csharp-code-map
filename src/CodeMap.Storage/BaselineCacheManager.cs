namespace CodeMap.Storage;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// File-based shared baseline cache. Copies SQLite baseline DB files between
/// the local directory and a configured shared filesystem path.
/// Atomic writes via temp-file-then-rename. No locks required because
/// baselines are immutable (same SHA always produces the same index).
/// </summary>
public sealed class BaselineCacheManager : IBaselineCacheManager
{
    private readonly BaselineDbFactory _localFactory;
    private readonly string? _sharedCacheDir;
    private readonly ILogger<BaselineCacheManager> _logger;

    public BaselineCacheManager(
        BaselineDbFactory localFactory,
        string? sharedCacheDir,
        ILogger<BaselineCacheManager> logger)
    {
        _localFactory = localFactory;
        _sharedCacheDir = sharedCacheDir;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsInCacheAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return Task.FromResult(false);
        return Task.FromResult(File.Exists(GetCachePath(repoId, commitSha)));
    }

    /// <inheritdoc/>
    /// <remarks>Atomic pull via temp-file-then-rename. Validates the pulled file using the SQLite magic header before moving to local path. Corrupt files are discarded (returns null).</remarks>
    public async Task<string?> PullAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return null;

        var cachePath = GetCachePath(repoId, commitSha);
        if (!File.Exists(cachePath)) return null;

        var localPath = _localFactory.GetDbPath(repoId, commitSha);
        if (File.Exists(localPath)) return localPath; // already local

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var tempPath = localPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await CopyFileAsync(cachePath, tempPath, ct).ConfigureAwait(false);

            // Validate: ensure the pulled file is a usable SQLite database
            if (!IsValidSqliteDb(tempPath))
            {
                _logger.LogWarning(
                    "Pulled baseline {Sha} from cache is corrupt — discarding",
                    commitSha.Value[..8]);
                return null;
            }

            File.Move(tempPath, localPath, overwrite: true);
            _logger.LogInformation("Pulled baseline {Sha} from shared cache", commitSha.Value[..8]);
            return localPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to pull baseline {Sha} from cache", commitSha.Value[..8]);
            return null;
        }
        finally
        {
            // Temp file is cleaned up if move didn't happen (error path)
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <see cref="SqliteConnection.ClearAllPools"/> before copying to release any pooled
    /// handles (Windows file-sharing compatibility). Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c>
    /// so the cache copy contains all committed data without WAL sidecar files.
    /// Overwrites corrupt cache entries (skips only when existing entry is a valid SQLite DB).
    /// </remarks>
    public async Task PushAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return;

        var localPath = _localFactory.GetDbPath(repoId, commitSha);
        if (!File.Exists(localPath)) return; // nothing to push

        var cachePath = GetCachePath(repoId, commitSha);
        // Skip only when cache already has a valid copy (allows overwriting corrupt entries)
        if (File.Exists(cachePath) && IsValidSqliteDb(cachePath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        // Release all pooled connections (e.g. from CreateBaselineAsync) so the
        // subsequent file copy can proceed without sharing conflicts on Windows.
        SqliteConnection.ClearAllPools();

        // Checkpoint WAL so all data is in the main DB file before copying
        try { CheckpointWal(localPath); }
        catch (Exception ex) { _logger.LogDebug(ex, "WAL checkpoint before push failed (non-fatal)"); }

        var tempPath = cachePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await CopyFileAsync(localPath, tempPath, ct).ConfigureAwait(false);
            File.Move(tempPath, cachePath, overwrite: true);
            _logger.LogInformation("Pushed baseline {Sha} to shared cache", commitSha.Value[..8]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push baseline {Sha} to cache", commitSha.Value[..8]);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    private string GetCachePath(RepoId repoId, CommitSha commitSha)
    {
        var safeRepo = SanitizeSegment(repoId.Value);
        return Path.Combine(_sharedCacheDir!, safeRepo, $"{commitSha.Value}.db");
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        using var src = File.OpenRead(source);
        using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static void CheckpointWal(string dbPath)
    {
        // Pooling=False ensures the connection is truly closed after dispose,
        // so no pooled handle remains to interfere with the subsequent file copy.
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        cmd.ExecuteNonQuery();
    }

    // SQLite 3 database files begin with this 16-byte magic header (null-terminated).
    private static readonly byte[] SqliteMagic =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    private static bool IsValidSqliteDb(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 100) return false; // minimum SQLite page header is 100 bytes
            var buf = new byte[16];
            return fs.Read(buf, 0, 16) == 16 && buf.SequenceEqual(SqliteMagic);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
}
