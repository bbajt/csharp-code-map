namespace CodeMap.Storage;

using CodeMap.Core.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// Opens (creating if needed) an overlay SQLite database for a given repo + workspace.
/// Database path: {baseDir}/{sanitizedRepoId}/{sanitizedWorkspaceId}.db
/// </summary>
public sealed class OverlayDbFactory
{
    private readonly string _baseDir;
    private readonly ILogger<OverlayDbFactory> _logger;

    public OverlayDbFactory(string baseDir, ILogger<OverlayDbFactory> logger)
    {
        _baseDir = baseDir;
        _logger = logger;
    }

    /// <summary>Returns the absolute path for the DB file (does not open it).</summary>
    public string GetDbPath(RepoId repoId, WorkspaceId workspaceId)
    {
        var safeRepo = SanitizeSegment(repoId.Value);
        var safeWs = SanitizeSegment(workspaceId.Value);
        return Path.Combine(_baseDir, safeRepo, $"{safeWs}.db");
    }

    /// <summary>
    /// Opens or creates the overlay DB, applying DDL on first creation.
    /// Caller must dispose the returned connection.
    /// </summary>
    public SqliteConnection OpenOrCreate(RepoId repoId, WorkspaceId workspaceId)
    {
        var path = GetDbPath(repoId, workspaceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        EnsureSchema(conn);
        return conn;
    }

    /// <summary>
    /// Opens an existing overlay DB read-write. Returns null if the file does not exist.
    /// Caller must dispose the returned connection.
    /// </summary>
    public SqliteConnection? OpenExisting(RepoId repoId, WorkspaceId workspaceId)
    {
        var path = GetDbPath(repoId, workspaceId);
        if (!File.Exists(path)) return null;

        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Deletes the overlay DB file and associated WAL/SHM files.
    /// No-op if the file does not exist.
    /// </summary>
    public void Delete(RepoId repoId, WorkspaceId workspaceId)
    {
        var path = GetDbPath(repoId, workspaceId);

        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort */ }
        }
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        // PRAGMAs must run outside any transaction (SQLite restriction)
        using var pragmaCmd = conn.CreateCommand();
        foreach (var pragma in OverlaySchemaDefinition.Pragmas)
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
            if (version != OverlaySchemaDefinition.Version)
                _logger.LogWarning(
                    "Overlay schema version mismatch: expected {Expected}, got {Actual}",
                    OverlaySchemaDefinition.Version, version);
            return;
        }

        _logger.LogDebug("Creating overlay schema (version {Version})", OverlaySchemaDefinition.Version);

        using var tx = conn.BeginTransaction();
        using var ddlCmd = conn.CreateCommand();
        ddlCmd.Transaction = tx;

        foreach (var statement in OverlaySchemaDefinition.DdlStatements)
        {
            ddlCmd.CommandText = statement;
            ddlCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
}
