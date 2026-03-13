namespace CodeMap.Storage;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// Opens (creating if needed) a baseline SQLite database for a given repo + commit.
/// Database path: {baseDir}/{sanitizedRepoId}/{commitSha}.db
/// </summary>
public sealed class BaselineDbFactory : IBaselineScanner
{
    private readonly string _baseDir;
    private readonly ILogger<BaselineDbFactory> _logger;

    public BaselineDbFactory(string baseDir, ILogger<BaselineDbFactory> logger)
    {
        _baseDir = baseDir;
        _logger = logger;
    }

    /// <summary>Returns the absolute path for the DB file (does not open it).</summary>
    public string GetDbPath(CodeMap.Core.Types.RepoId repoId, CodeMap.Core.Types.CommitSha commitSha)
    {
        var safeRepo = SanitizeSegment(repoId.Value);
        var safeSha = commitSha.Value; // Already 40 hex chars — safe
        return Path.Combine(_baseDir, safeRepo, $"{safeSha}.db");
    }

    /// <summary>
    /// Opens or creates the baseline DB, applying DDL on first creation.
    /// Caller must dispose the returned connection.
    /// </summary>
    public SqliteConnection OpenOrCreate(CodeMap.Core.Types.RepoId repoId, CodeMap.Core.Types.CommitSha commitSha)
    {
        var path = GetDbPath(repoId, commitSha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        EnsureSchema(conn);
        return conn;
    }

    /// <summary>
    /// Opens an existing DB read-write. Returns null if the file does not exist.
    /// Caller must dispose the returned connection.
    /// </summary>
    public SqliteConnection? OpenExisting(CodeMap.Core.Types.RepoId repoId, CodeMap.Core.Types.CommitSha commitSha)
    {
        var path = GetDbPath(repoId, commitSha);
        if (!File.Exists(path)) return null;

        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        // PRAGMAs must run outside any transaction (SQLite restriction)
        using var pragmaCmd = conn.CreateCommand();
        foreach (var pragma in SchemaDefinition.Pragmas)
        {
            pragmaCmd.CommandText = pragma;
            pragmaCmd.ExecuteNonQuery();
        }

        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'";
        var exists = (long)checkCmd.ExecuteScalar()! > 0;

        if (exists)
        {
            using var verCmd = conn.CreateCommand();
            verCmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
            var version = (long)verCmd.ExecuteScalar()!;
            if (version != SchemaDefinition.Version)
                _logger.LogWarning(
                    "Schema version mismatch: expected {Expected}, got {Actual}",
                    SchemaDefinition.Version, version);
            return;
        }

        _logger.LogDebug("Creating baseline schema (version {Version})", SchemaDefinition.Version);

        using var tx = conn.BeginTransaction();
        using var ddlCmd = conn.CreateCommand();
        ddlCmd.Transaction = tx;

        foreach (var statement in SchemaDefinition.DdlStatements)
        {
            ddlCmd.CommandText = statement;
            ddlCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Returns the directory that holds all baselines for a given repo.
    /// The directory may not exist if no baselines have been created yet.
    /// </summary>
    public string GetBaselineDirectory(CodeMap.Core.Types.RepoId repoId)
        => Path.Combine(_baseDir, SanitizeSegment(repoId.Value));

    /// <summary>
    /// Scans the baseline directory and returns metadata for every cached baseline.
    /// Non-.db files and files whose name is not a 40-character hex SHA are silently skipped.
    /// Results are sorted newest-first by file creation time.
    /// </summary>
    public async Task<IReadOnlyList<BaselineInfo>> ListBaselinesAsync(
        CodeMap.Core.Types.RepoId repoId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var dir = GetBaselineDirectory(repoId);
        if (!Directory.Exists(dir))
            return [];

        var baselines = new List<BaselineInfo>();

        foreach (var file in Directory.GetFiles(dir, "*.db"))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.Length != 40 || !IsHexString(fileName))
                continue;

            // Checkpoint the WAL so FileInfo.Length reflects the true database size
            // rather than the inflated size caused by -wal sidecar files.
            try
            {
                var connStr = $"Data Source={file};Pooling=False";
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // If checkpoint fails (e.g. locked), fall back to raw file size
            }

            var info = new FileInfo(file);
            baselines.Add(new BaselineInfo(
                CommitSha: CodeMap.Core.Types.CommitSha.From(fileName.ToLowerInvariant()),
                CreatedAt: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                SizeBytes: info.Length,
                IsCurrentHead: false,       // Caller enriches
                IsActiveWorkspaceBase: false // Caller enriches
            ));
        }

        return baselines
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Removes old baselines according to retention rules. Protected baselines
    /// (current HEAD and workspace-referenced) are never deleted.
    /// </summary>
    /// <param name="repoId">Repository whose baselines to clean up.</param>
    /// <param name="currentHead">Current HEAD commit — always kept.</param>
    /// <param name="workspaceBaseCommits">Baseline SHAs referenced by active workspaces — always kept.</param>
    /// <param name="keepCount">Keep the N most-recently-created baselines (default 5).</param>
    /// <param name="olderThanDays">Remove baselines older than N days (optional).</param>
    /// <param name="dryRun">If true, report what would be deleted without deleting (default true).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CleanupResponse> CleanupBaselinesAsync(
        CodeMap.Core.Types.RepoId repoId,
        CodeMap.Core.Types.CommitSha currentHead,
        IReadOnlySet<CodeMap.Core.Types.CommitSha> workspaceBaseCommits,
        int keepCount = 5,
        int? olderThanDays = null,
        bool dryRun = true,
        CancellationToken ct = default)
    {
        var baselines = await ListBaselinesAsync(repoId, ct).ConfigureAwait(false);

        // Build protected set
        var protectedShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentHead.Value
        };
        foreach (var ws in workspaceBaseCommits)
            protectedShas.Add(ws.Value);

        // Start with all non-protected candidates
        var candidates = baselines
            .Where(b => !protectedShas.Contains(b.CommitSha.Value))
            .ToList();

        // Apply age filter
        if (olderThanDays.HasValue)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays.Value);
            candidates = candidates.Where(b => b.CreatedAt < cutoff).ToList();
        }

        // Apply count filter: keep the newest keepCount from the FULL list (protected or not)
        var keepShas = baselines
            .OrderByDescending(b => b.CreatedAt)
            .Take(keepCount)
            .Select(b => b.CommitSha.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        candidates = candidates
            .Where(b => !keepShas.Contains(b.CommitSha.Value))
            .ToList();

        long bytesReclaimed = 0;
        var removed = new List<CodeMap.Core.Types.CommitSha>();

        if (!dryRun)
        {
            foreach (var baseline in candidates)
            {
                var path = GetDbPath(repoId, baseline.CommitSha);
                try
                {
                    bytesReclaimed += DeleteBaselineFiles(path);
                    removed.Add(baseline.CommitSha);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // On Windows, in-use SQLite files cannot be deleted.
                    // Log and skip rather than crashing.
                    _logger.LogWarning(ex,
                        "CleanupBaselines: could not delete baseline {Sha} (file in use?)",
                        baseline.CommitSha.Value[..8]);
                }
            }
        }
        else
        {
            bytesReclaimed = candidates.Sum(b => b.SizeBytes);
            removed = candidates.Select(b => b.CommitSha).ToList();
        }

        var removedSet = removed.Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = baselines
            .Where(b => !removedSet.Contains(b.CommitSha.Value))
            .Select(b => b.CommitSha)
            .ToList();

        return new CleanupResponse(removed.Count, bytesReclaimed, removed, kept, dryRun);
    }

    private static long DeleteBaselineFiles(string dbPath)
    {
        long size = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = dbPath + suffix;
            if (!File.Exists(file)) continue;
            size += new FileInfo(file).Length;
            File.Delete(file);
        }
        return size;
    }

    private static bool IsHexString(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
}
