namespace CodeMap.Storage;

using System.Text.Json;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// SQLite-backed implementation of <see cref="IOverlayStore"/> for workspace overlays.
/// Each overlay is a mutable DB keyed by (repoId, workspaceId).
/// </summary>
public sealed class OverlayStore : IOverlayStore
{
    private readonly OverlayDbFactory _factory;
    private readonly ILogger<OverlayStore> _logger;

    public OverlayStore(OverlayDbFactory factory, ILogger<OverlayStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // =========================================================================
    // Write path (T02)
    // =========================================================================

    /// <inheritdoc/>
    public async Task CreateOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CommitSha baselineCommitSha,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Creating overlay {RepoId}/{WorkspaceId} based on {Sha}",
            repoId.Value, workspaceId.Value, baselineCommitSha.Value[..8]);

        using var conn = _factory.OpenOrCreate(repoId, workspaceId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO overlay_meta(key, value) VALUES ('revision', '0');
            INSERT OR REPLACE INTO overlay_meta(key, value) VALUES ('baseline_commit_sha', $sha);
            INSERT OR REPLACE INTO overlay_meta(key, value) VALUES ('workspace_id', $ws);
            """;
        cmd.Parameters.AddWithValue("$sha", baselineCommitSha.Value);
        cmd.Parameters.AddWithValue("$ws", workspaceId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>Deletes all existing overlay data for reindexed files before inserting new data, ensuring a clean slate on re-index. All mutations run in a single transaction; FTS is rebuilt after commit.</remarks>
    public async Task ApplyDeltaAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        OverlayDelta delta,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Applying overlay delta {RepoId}/{WorkspaceId}: {Symbols} symbols, {Deleted} deleted, rev→{Rev}",
            repoId.Value, workspaceId.Value,
            delta.AddedOrUpdatedSymbols.Count, delta.DeletedSymbolIds.Count, delta.NewRevision);

        using var conn = _factory.OpenOrCreate(repoId, workspaceId);
        using var tx = conn.BeginTransaction();
        try
        {
            // 1. Upsert reindexed files
            await UpsertFilesAsync(conn, tx, delta.ReindexedFiles, ct);

            // 2. Remove old overlay symbols/refs/type-relations/facts for re-reindexed files
            //    (ensures clean slate when a file is reindexed a second time)
            //    type_relations must be deleted BEFORE symbols (its delete query joins on symbols)
            var fileIds = BuildFileIdMap(delta.ReindexedFiles);
            await DeleteTypeRelationsForFilesAsync(conn, tx, fileIds.Values, ct);
            await DeleteFactsForFilesAsync(conn, tx, fileIds.Values, ct);
            await DeleteSymbolsForFilesAsync(conn, tx, fileIds.Values, ct);
            await DeleteRefsForFilesAsync(conn, tx, fileIds.Values, ct);

            // 3. Insert new/updated symbols
            await InsertSymbolsAsync(conn, tx, delta.AddedOrUpdatedSymbols, fileIds, ct);

            // 4. Insert new references
            await InsertRefsAsync(conn, tx, delta.AddedOrUpdatedReferences, fileIds, ct);

            // 4b. Insert type relations
            if (delta.TypeRelations is { Count: > 0 } typeRelations)
                await InsertTypeRelationsAsync(conn, tx, typeRelations, ct);

            // 4c. Insert facts
            if (delta.Facts is { Count: > 0 } facts)
                await InsertFactsAsync(conn, tx, facts, fileIds, ct);

            // 5. Record deleted symbols (and un-delete any re-added ones)
            await RecordDeletedSymbolsAsync(conn, tx, delta.DeletedSymbolIds, delta.NewRevision, ct);
            await UndeleteSymbolsAsync(conn, tx, delta.AddedOrUpdatedSymbols, ct);

            // 6. Update revision
            await SetRevisionAsync(conn, tx, delta.NewRevision, ct);

            // 7. Store SemanticLevel if provided
            if (delta.SemanticLevel.HasValue)
                await SetOverlaySemanticLevelAsync(conn, tx, delta.SemanticLevel.Value, ct);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        // 7. Rebuild FTS after commit (content= tables require explicit rebuild)
        await RebuildFtsAsync(conn, ct);
    }

    /// <inheritdoc/>
    public async Task ResetOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Resetting overlay {RepoId}/{WorkspaceId}", repoId.Value, workspaceId.Value);

        using var conn = _factory.OpenOrCreate(repoId, workspaceId);

        // Delete all data (preserve metadata keys — just reset revision)
        using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = """
            DELETE FROM refs;
            DELETE FROM symbols;
            DELETE FROM files;
            DELETE FROM deleted_symbols;
            UPDATE overlay_meta SET value = '0' WHERE key = 'revision';
            """;
        await deleteCmd.ExecuteNonQueryAsync(ct);

        await RebuildFtsAsync(conn, ct);
    }

    /// <inheritdoc/>
    public Task DeleteOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Deleting overlay {RepoId}/{WorkspaceId}", repoId.Value, workspaceId.Value);

        SqliteConnection.ClearAllPools();
        _factory.Delete(repoId, workspaceId);
        return Task.CompletedTask;
    }

    // =========================================================================
    // Read path (T03)
    // =========================================================================

    /// <inheritdoc/>
    public Task<bool> OverlayExistsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        var path = _factory.GetDbPath(repoId, workspaceId);
        return Task.FromResult(File.Exists(path));
    }

    /// <inheritdoc/>
    public async Task<int> GetRevisionAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM overlay_meta WHERE key = 'revision' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s && int.TryParse(s, out var rev) ? rev : 0;
    }

    /// <inheritdoc/>
    public async Task<SymbolCard?> GetOverlaySymbolAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   s.namespace, s.containing_type, f.path, s.span_start, s.span_end,
                   s.visibility, s.confidence, s.content_hash, s.stable_id
            FROM symbols s
            JOIN files f ON s.file_id = f.file_id
            WHERE s.symbol_id = $symbol_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$symbol_id", symbolId.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return ReadSymbolCard(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SymbolSearchHit>> SearchOverlaySymbolsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        string query,
        SymbolSearchFilters? filters,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder("""
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   f.path, s.span_start, rank AS score
            FROM symbols_fts
            JOIN symbols s ON symbols_fts.rowid = s.rowid
            JOIN files f ON s.file_id = f.file_id
            WHERE symbols_fts MATCH $query
            """);

        ApplyFilters(cmd, filters, sql);

        sql.Append(" ORDER BY rank LIMIT $limit");
        cmd.Parameters.AddWithValue("$query", query);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var hits = new List<SymbolSearchHit>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            hits.Add(new SymbolSearchHit(
                SymbolId: SymbolId.From(reader.GetString(0)),
                FullyQualifiedName: reader.GetString(1),
                Kind: Enum.Parse<SymbolKind>(reader.GetString(2)),
                Signature: reader.GetString(3),
                DocumentationSnippet: reader.IsDBNull(4) ? null :
                    reader.GetString(4) is { Length: > 200 } d ? d[..200] : reader.GetString(4),
                FilePath: FilePath.From(reader.GetString(5)),
                Line: reader.GetInt32(6),
                Score: reader.GetDouble(7)));
        }
        return hits;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredReference>> GetOverlayReferencesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        RefKind? kind,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder("""
            SELECT r.ref_kind, r.from_symbol_id, f.path, r.loc_start, r.loc_end,
                   r.resolution_state, r.to_name, r.to_container_hint
            FROM refs r
            JOIN files f ON r.file_id = f.file_id
            WHERE r.to_symbol_id = $symbol_id
            """);

        if (kind.HasValue)
        {
            sql.Append(" AND r.ref_kind = $ref_kind");
            cmd.Parameters.AddWithValue("$ref_kind", kind.Value.ToString());
        }

        sql.Append(" ORDER BY f.path, r.loc_start LIMIT $limit");
        cmd.Parameters.AddWithValue("$symbol_id", symbolId.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var refs = new List<StoredReference>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var resState = Enum.TryParse<ResolutionState>(reader.GetString(5), ignoreCase: true, out var rs)
                ? rs : ResolutionState.Resolved;
            refs.Add(new StoredReference(
                Kind: Enum.Parse<RefKind>(reader.GetString(0)),
                FromSymbol: SymbolId.From(reader.GetString(1)),
                FilePath: FilePath.From(reader.GetString(2)),
                LineStart: reader.GetInt32(3),
                LineEnd: reader.GetInt32(4),
                Excerpt: null,
                ResolutionState: resState,
                ToName: reader.IsDBNull(6) ? null : reader.GetString(6),
                ToContainerHint: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return refs;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<SymbolId>> GetDeletedSymbolIdsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return new HashSet<SymbolId>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT symbol_id FROM deleted_symbols";
        using var reader = await cmd.ExecuteReaderAsync(ct);

        var ids = new HashSet<SymbolId>();
        while (await reader.ReadAsync(ct))
            ids.Add(SymbolId.From(reader.GetString(0)));
        return ids;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<FilePath>> GetOverlayFilePathsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return new HashSet<FilePath>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM files";
        using var reader = await cmd.ExecuteReaderAsync(ct);

        var paths = new HashSet<FilePath>();
        while (await reader.ReadAsync(ct))
            paths.Add(FilePath.From(reader.GetString(0)));
        return paths;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingOverlayReferencesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        RefKind? kind,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder("""
            SELECT r.ref_kind, r.to_symbol_id, f.path, r.loc_start, r.loc_end,
                   r.resolution_state, r.to_name, r.to_container_hint
            FROM refs r
            JOIN files f ON r.file_id = f.file_id
            WHERE r.from_symbol_id = $symbol_id
            """);

        if (kind.HasValue)
        {
            sql.Append(" AND r.ref_kind = $ref_kind");
            cmd.Parameters.AddWithValue("$ref_kind", kind.Value.ToString());
        }

        sql.Append(" ORDER BY f.path, r.loc_start LIMIT $limit");
        cmd.Parameters.AddWithValue("$symbol_id", symbolId.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var refs = new List<StoredOutgoingReference>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var resState = Enum.TryParse<ResolutionState>(reader.GetString(5), ignoreCase: true, out var rs)
                ? rs : ResolutionState.Resolved;
            refs.Add(new StoredOutgoingReference(
                Kind: Enum.Parse<RefKind>(reader.GetString(0)),
                ToSymbol: reader.GetString(1) is { Length: > 0 } s ? SymbolId.From(s) : SymbolId.Empty,
                FilePath: FilePath.From(reader.GetString(2)),
                LineStart: reader.GetInt32(3),
                LineEnd: reader.GetInt32(4),
                ResolutionState: resState,
                ToName: reader.IsDBNull(6) ? null : reader.GetString(6),
                ToContainerHint: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return refs;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredTypeRelation>> GetOverlayTypeRelationsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_symbol_id, related_symbol_id, relation_kind, display_name
            FROM type_relations
            WHERE type_symbol_id = $symbol_id
            """;
        cmd.Parameters.AddWithValue("$symbol_id", symbolId.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<StoredTypeRelation>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new StoredTypeRelation(
                TypeSymbolId: SymbolId.From(reader.GetString(0)),
                RelatedSymbolId: SymbolId.From(reader.GetString(1)),
                RelationKind: Enum.Parse<TypeRelationKind>(reader.GetString(2)),
                DisplayName: reader.GetString(3)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredTypeRelation>> GetOverlayDerivedTypesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        // JOIN with symbols to get the derived type's display name (not the related type's name)
        cmd.CommandText = """
            SELECT tr.type_symbol_id, tr.related_symbol_id, tr.relation_kind,
                   COALESCE(s.fqname, tr.type_symbol_id) AS derived_name
            FROM type_relations tr
            LEFT JOIN symbols s ON s.symbol_id = tr.type_symbol_id
            WHERE tr.related_symbol_id = $symbol_id
            """;
        cmd.Parameters.AddWithValue("$symbol_id", symbolId.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<StoredTypeRelation>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new StoredTypeRelation(
                TypeSymbolId: SymbolId.From(reader.GetString(0)),
                RelatedSymbolId: SymbolId.From(reader.GetString(1)),
                RelationKind: Enum.Parse<TypeRelationKind>(reader.GetString(2)),
                DisplayName: reader.GetString(3)));
        }
        return result;
    }

    // =========================================================================
    // Write helpers
    // =========================================================================

    private static Dictionary<string, string> BuildFileIdMap(IReadOnlyList<ExtractedFile> files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            map[f.Path.Value] = f.FileId;
        return map;
    }

    private static async Task UpsertFilesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<ExtractedFile> files,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO files (file_id, path, sha256, project_id)
            VALUES ($file_id, $path, $sha256, $project_id)
            """;

        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pSha256 = cmd.Parameters.Add("$sha256", SqliteType.Text);
        var pProjectId = cmd.Parameters.Add("$project_id", SqliteType.Text);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            pFileId.Value = file.FileId;
            pPath.Value = file.Path.Value;
            pSha256.Value = file.Sha256Hash;
            pProjectId.Value = (object?)file.ProjectName ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteSymbolsForFilesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IEnumerable<string> fileIds,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM symbols WHERE file_id = $file_id";
        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);

        foreach (var fileId in fileIds)
        {
            ct.ThrowIfCancellationRequested();
            pFileId.Value = fileId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteRefsForFilesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IEnumerable<string> fileIds,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM refs WHERE file_id = $file_id";
        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);

        foreach (var fileId in fileIds)
        {
            ct.ThrowIfCancellationRequested();
            pFileId.Value = fileId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteTypeRelationsForFilesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IEnumerable<string> fileIds,
        CancellationToken ct)
    {
        // Delete type relations for types that belong to the reindexed files
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM type_relations
            WHERE type_symbol_id IN (
                SELECT symbol_id FROM symbols WHERE file_id = $file_id
            )
            """;
        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);

        foreach (var fileId in fileIds)
        {
            ct.ThrowIfCancellationRequested();
            pFileId.Value = fileId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertTypeRelationsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<ExtractedTypeRelation> relations,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO type_relations
                (type_symbol_id, related_symbol_id, relation_kind, display_name,
                 stable_type_id, stable_related_id)
            VALUES
                ($type_symbol_id, $related_symbol_id, $relation_kind, $display_name,
                 $stable_type_id, $stable_related_id)
            """;

        var pType = cmd.Parameters.Add("$type_symbol_id", SqliteType.Text);
        var pRelated = cmd.Parameters.Add("$related_symbol_id", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$relation_kind", SqliteType.Text);
        var pDisplay = cmd.Parameters.Add("$display_name", SqliteType.Text);
        var pStableType = cmd.Parameters.Add("$stable_type_id", SqliteType.Text);
        var pStableRelated = cmd.Parameters.Add("$stable_related_id", SqliteType.Text);

        foreach (var r in relations)
        {
            ct.ThrowIfCancellationRequested();
            pType.Value = r.TypeSymbolId.Value;
            pRelated.Value = r.RelatedSymbolId.Value;
            pKind.Value = r.RelationKind.ToString();
            pDisplay.Value = r.DisplayName;
            pStableType.Value = r.StableTypeId.HasValue && !r.StableTypeId.Value.IsEmpty
                                   ? (object)r.StableTypeId.Value.Value : DBNull.Value;
            pStableRelated.Value = r.StableRelatedId.HasValue && !r.StableRelatedId.Value.IsEmpty
                                   ? (object)r.StableRelatedId.Value.Value : DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertSymbolsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<SymbolCard> symbols,
        Dictionary<string, string> fileIdByPath,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO symbols
                (symbol_id, fqname, kind, file_id, span_start, span_end,
                 signature, documentation, namespace, containing_type,
                 visibility, confidence, content_hash, stable_id)
            VALUES
                ($symbol_id, $fqname, $kind, $file_id, $span_start, $span_end,
                 $signature, $documentation, $namespace, $containing_type,
                 $visibility, $confidence, $content_hash, $stable_id)
            """;

        var pSymbolId = cmd.Parameters.Add("$symbol_id", SqliteType.Text);
        var pFqname = cmd.Parameters.Add("$fqname", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);
        var pSpanStart = cmd.Parameters.Add("$span_start", SqliteType.Integer);
        var pSpanEnd = cmd.Parameters.Add("$span_end", SqliteType.Integer);
        var pSignature = cmd.Parameters.Add("$signature", SqliteType.Text);
        var pDocumentation = cmd.Parameters.Add("$documentation", SqliteType.Text);
        var pNamespace = cmd.Parameters.Add("$namespace", SqliteType.Text);
        var pContainingType = cmd.Parameters.Add("$containing_type", SqliteType.Text);
        var pVisibility = cmd.Parameters.Add("$visibility", SqliteType.Text);
        var pConfidence = cmd.Parameters.Add("$confidence", SqliteType.Text);
        var pContentHash = cmd.Parameters.Add("$content_hash", SqliteType.Text);
        var pStableId = cmd.Parameters.Add("$stable_id", SqliteType.Text);

        foreach (var s in symbols)
        {
            ct.ThrowIfCancellationRequested();

            if (!fileIdByPath.TryGetValue(s.FilePath.Value, out var fileId))
                continue; // Skip symbols from files not in this delta

            pSymbolId.Value = s.SymbolId.Value;
            pFqname.Value = s.FullyQualifiedName;
            pKind.Value = s.Kind.ToString();
            pFileId.Value = fileId;
            pSpanStart.Value = s.SpanStart;
            pSpanEnd.Value = s.SpanEnd;
            pSignature.Value = (object?)s.Signature ?? DBNull.Value;
            pDocumentation.Value = (object?)s.Documentation ?? DBNull.Value;
            pNamespace.Value = s.Namespace;
            pContainingType.Value = (object?)s.ContainingType ?? DBNull.Value;
            pVisibility.Value = s.Visibility;
            pConfidence.Value = s.Confidence.ToString();
            pContentHash.Value = ComputeContentHash(s);
            pStableId.Value = s.StableId.HasValue && !s.StableId.Value.IsEmpty
                                    ? (object)s.StableId.Value.Value : DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertRefsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<ExtractedReference> refs,
        Dictionary<string, string> fileIdByPath,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO refs
                (from_symbol_id, to_symbol_id, ref_kind, file_id, loc_start, loc_end,
                 resolution_state, to_name, to_container_hint, stable_from_id, stable_to_id)
            VALUES
                ($from_symbol_id, $to_symbol_id, $ref_kind, $file_id, $loc_start, $loc_end,
                 $resolution_state, $to_name, $to_container_hint, $stable_from_id, $stable_to_id)
            """;

        var pFrom = cmd.Parameters.Add("$from_symbol_id", SqliteType.Text);
        var pTo = cmd.Parameters.Add("$to_symbol_id", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$ref_kind", SqliteType.Text);
        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);
        var pStart = cmd.Parameters.Add("$loc_start", SqliteType.Integer);
        var pEnd = cmd.Parameters.Add("$loc_end", SqliteType.Integer);
        var pResState = cmd.Parameters.Add("$resolution_state", SqliteType.Text);
        var pToName = cmd.Parameters.Add("$to_name", SqliteType.Text);
        var pHint = cmd.Parameters.Add("$to_container_hint", SqliteType.Text);
        var pStableFrom = cmd.Parameters.Add("$stable_from_id", SqliteType.Text);
        var pStableTo = cmd.Parameters.Add("$stable_to_id", SqliteType.Text);

        foreach (var r in refs)
        {
            ct.ThrowIfCancellationRequested();

            if (!fileIdByPath.TryGetValue(r.FilePath.Value, out var fileId))
                continue; // Skip refs from files not in this delta

            pFrom.Value = r.FromSymbol.Value;
            pTo.Value = r.ToSymbol.Value;
            pKind.Value = r.Kind.ToString();
            pFileId.Value = fileId;
            pStart.Value = r.LineStart;
            pEnd.Value = r.LineEnd;
            pResState.Value = r.ResolutionState.ToString().ToLowerInvariant();
            pToName.Value = (object?)r.ToName ?? DBNull.Value;
            pHint.Value = (object?)r.ToContainerHint ?? DBNull.Value;
            pStableFrom.Value = r.StableFromId.HasValue && !r.StableFromId.Value.IsEmpty
                                ? (object)r.StableFromId.Value.Value : DBNull.Value;
            pStableTo.Value = r.StableToId.HasValue && !r.StableToId.Value.IsEmpty
                                ? (object)r.StableToId.Value.Value : DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task RecordDeletedSymbolsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<SymbolId> deletedIds,
        int revision,
        CancellationToken ct)
    {
        if (deletedIds.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO deleted_symbols(symbol_id, deleted_at_revision)
            VALUES ($symbol_id, $revision)
            """;
        var pId = cmd.Parameters.Add("$symbol_id", SqliteType.Text);
        var pRev = cmd.Parameters.Add("$revision", SqliteType.Integer);
        pRev.Value = revision;

        foreach (var id in deletedIds)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = id.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UndeleteSymbolsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<SymbolCard> addedSymbols,
        CancellationToken ct)
    {
        if (addedSymbols.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM deleted_symbols WHERE symbol_id = $symbol_id";
        var pId = cmd.Parameters.Add("$symbol_id", SqliteType.Text);

        foreach (var s in addedSymbols)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = s.SymbolId.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SetRevisionAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        int revision,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE overlay_meta SET value = $revision WHERE key = 'revision'";
        cmd.Parameters.AddWithValue("$revision", revision.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RebuildFtsAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // =========================================================================
    // Read helpers
    // =========================================================================

    private static SymbolCard ReadSymbolCard(SqliteDataReader reader)
    {
        StableId? stableId = null;
        if (reader.FieldCount > 13 && !reader.IsDBNull(13))
            stableId = new StableId(reader.GetString(13));

        return SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(reader.GetString(0)),
            fullyQualifiedName: reader.GetString(1),
            kind: Enum.Parse<SymbolKind>(reader.GetString(2)),
            signature: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            @namespace: reader.GetString(5),
            filePath: FilePath.From(reader.GetString(7)),
            spanStart: reader.GetInt32(8),
            spanEnd: reader.GetInt32(9),
            visibility: reader.GetString(10),
            confidence: Enum.Parse<Confidence>(reader.GetString(11)),
            documentation: reader.IsDBNull(4) ? null : reader.GetString(4),
            containingType: reader.IsDBNull(6) ? null : reader.GetString(6))
            with
        { StableId = stableId };
    }

    private static void ApplyFilters(
        SqliteCommand cmd,
        SymbolSearchFilters? filters,
        System.Text.StringBuilder sql)
    {
        if (filters is null) return;

        if (filters.Kinds is { Count: > 0 })
        {
            var placeholders = filters.Kinds
                .Select((_, i) => $"$kind{i}")
                .ToList();
            sql.Append($" AND s.kind IN ({string.Join(',', placeholders)})");
            for (int i = 0; i < filters.Kinds.Count; i++)
                cmd.Parameters.AddWithValue($"$kind{i}", filters.Kinds[i].ToString());
        }

        if (!string.IsNullOrWhiteSpace(filters.Namespace))
        {
            sql.Append(" AND s.namespace LIKE $ns_prefix");
            cmd.Parameters.AddWithValue("$ns_prefix", filters.Namespace.TrimEnd('.') + "%");
        }

        if (!string.IsNullOrWhiteSpace(filters.FilePath))
        {
            sql.Append(" AND f.path LIKE $fp_prefix");
            cmd.Parameters.AddWithValue("$fp_prefix", filters.FilePath + "%");
        }
    }

    /// <summary>
    /// Computes a 16-char content hash for change detection.
    /// Input: "{Kind}|{Signature}|{Documentation}|{SpanStart}-{SpanEnd}|{Visibility}"
    /// </summary>
    private static string ComputeContentHash(SymbolCard s)
    {
        var input = $"{s.Kind}|{s.Signature}|{s.Documentation}|{s.SpanStart}-{s.SpanEnd}|{s.Visibility}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static async Task SetOverlaySemanticLevelAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        SemanticLevel level,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO overlay_meta(key, value) VALUES ('semantic_level', $value)";
        cmd.Parameters.AddWithValue("$value", level.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<SemanticLevel?> GetOverlaySemanticLevelAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM overlay_meta WHERE key = 'semantic_level' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string s) return null;
        return Enum.TryParse<SemanticLevel>(s, out var level) ? level : null;
    }

    /// <inheritdoc/>
    public async Task<SymbolCard?> GetSymbolByStableIdAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        StableId stableId,
        CancellationToken ct = default)
    {
        if (stableId.IsEmpty) return null;

        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   s.namespace, s.containing_type, f.path, s.span_start, s.span_end,
                   s.visibility, s.confidence, s.content_hash, s.stable_id
            FROM symbols s
            JOIN files f ON s.file_id = f.file_id
            WHERE s.stable_id = $stable_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$stable_id", stableId.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadSymbolCard(reader);
    }

    // =========================================================================
    // Facts
    // =========================================================================

    private static async Task DeleteFactsForFilesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IEnumerable<string> fileIds,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM facts WHERE file_id = $file_id";
        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);

        foreach (var fileId in fileIds)
        {
            ct.ThrowIfCancellationRequested();
            pFileId.Value = fileId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertFactsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<ExtractedFact> facts,
        Dictionary<string, string> fileIdByPath,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO facts
                (symbol_id, stable_id, fact_kind, value, file_id, loc_start, loc_end, confidence)
            VALUES
                ($symbol_id, $stable_id, $fact_kind, $value, $file_id, $loc_start, $loc_end, $confidence)
            """;

        var pSymbol = cmd.Parameters.Add("$symbol_id", SqliteType.Text);
        var pStable = cmd.Parameters.Add("$stable_id", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$fact_kind", SqliteType.Text);
        var pValue = cmd.Parameters.Add("$value", SqliteType.Text);
        var pFile = cmd.Parameters.Add("$file_id", SqliteType.Text);
        var pStart = cmd.Parameters.Add("$loc_start", SqliteType.Integer);
        var pEnd = cmd.Parameters.Add("$loc_end", SqliteType.Integer);
        var pConf = cmd.Parameters.Add("$confidence", SqliteType.Text);

        foreach (var f in facts)
        {
            ct.ThrowIfCancellationRequested();
            // Skip facts for files not in this delta
            if (!fileIdByPath.TryGetValue(f.FilePath.Value, out var fileId))
                continue;

            pSymbol.Value = f.SymbolId.Value;
            pStable.Value = f.StableId.HasValue && !f.StableId.Value.IsEmpty
                            ? (object)f.StableId.Value.Value : DBNull.Value;
            pKind.Value = f.Kind.ToString();
            pValue.Value = f.Value;
            pFile.Value = fileId;
            pStart.Value = f.LineStart;
            pEnd.Value = f.LineEnd;
            pConf.Value = f.Confidence.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredFact>> GetOverlayFactsByKindAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        FactKind kind,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.symbol_id, f.stable_id, f.fact_kind, f.value,
                   fi.path, f.loc_start, f.loc_end, f.confidence
            FROM facts f
            JOIN files fi ON f.file_id = fi.file_id
            WHERE f.fact_kind = $kind
            ORDER BY f.value
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<StoredFact>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            StableId? stableId = reader.IsDBNull(1) ? null
                : new StableId(reader.GetString(1));
            result.Add(new StoredFact(
                SymbolId: SymbolId.From(reader.GetString(0)),
                StableId: stableId,
                Kind: Enum.Parse<FactKind>(reader.GetString(2)),
                Value: reader.GetString(3),
                FilePath: FilePath.From(reader.GetString(4)),
                LineStart: reader.GetInt32(5),
                LineEnd: reader.GetInt32(6),
                Confidence: Enum.Parse<Confidence>(reader.GetString(7))));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredFact>> GetOverlayFactsForSymbolAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.symbol_id, f.stable_id, f.fact_kind, f.value,
                   fi.path, f.loc_start, f.loc_end, f.confidence
            FROM facts f
            JOIN files fi ON f.file_id = fi.file_id
            WHERE f.symbol_id = $symbolId
            ORDER BY f.fact_kind, f.loc_start
            """;
        cmd.Parameters.AddWithValue("$symbolId", symbolId.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<StoredFact>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            StableId? stableId = reader.IsDBNull(1) ? null
                : new StableId(reader.GetString(1));
            result.Add(new StoredFact(
                SymbolId: SymbolId.From(reader.GetString(0)),
                StableId: stableId,
                Kind: Enum.Parse<FactKind>(reader.GetString(2)),
                Value: reader.GetString(3),
                FilePath: FilePath.From(reader.GetString(4)),
                LineStart: reader.GetInt32(5),
                LineEnd: reader.GetInt32(6),
                Confidence: Enum.Parse<Confidence>(reader.GetString(7))));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<int> GetOverlayFactCountAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM facts";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long n ? (int)n : 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UnresolvedEdge>> GetOverlayUnresolvedEdgesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        IReadOnlyList<FilePath> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return [];

        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        var placeholders = string.Join(", ", filePaths.Select((_, i) => $"$p{i}"));
        cmd.CommandText = $"""
            SELECT r.from_symbol_id, r.to_name, r.to_container_hint, r.ref_kind,
                   r.file_id, r.loc_start, r.loc_end
            FROM refs r
            JOIN files f ON r.file_id = f.file_id
            WHERE r.resolution_state = 'unresolved'
              AND f.path IN ({placeholders})
            """;
        for (int i = 0; i < filePaths.Count; i++)
            cmd.Parameters.AddWithValue($"$p{i}", filePaths[i].Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<UnresolvedEdge>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new UnresolvedEdge(
                FromSymbolId: reader.GetString(0),
                ToName: reader.IsDBNull(1) ? null : reader.GetString(1),
                ToContainerHint: reader.IsDBNull(2) ? null : reader.GetString(2),
                RefKind: reader.GetString(3),
                FileId: reader.GetString(4),
                LocStart: reader.GetInt32(5),
                LocEnd: reader.GetInt32(6)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task UpgradeOverlayEdgeAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        EdgeUpgrade upgrade,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, workspaceId);
        if (conn is null) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE refs
            SET resolution_state    = 'resolved',
                to_symbol_id        = $toSymbolId,
                stable_to_id        = $stableToId,
                to_name             = NULL,
                to_container_hint   = NULL
            WHERE from_symbol_id    = $fromSymbolId
              AND file_id           = $fileId
              AND loc_start         = $locStart
              AND resolution_state  = 'unresolved'
            """;
        cmd.Parameters.AddWithValue("$toSymbolId", upgrade.ResolvedToSymbolId.Value);
        cmd.Parameters.AddWithValue("$stableToId", (object?)upgrade.ResolvedStableToId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fromSymbolId", upgrade.FromSymbolId);
        cmd.Parameters.AddWithValue("$fileId", upgrade.FileId);
        cmd.Parameters.AddWithValue("$locStart", upgrade.LocStart);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
