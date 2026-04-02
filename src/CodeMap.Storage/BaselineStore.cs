namespace CodeMap.Storage;

using System.Text.Json;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// SQLite-backed implementation of <see cref="ISymbolStore"/> for baseline indexes.
/// Each baseline is an immutable DB keyed by (repoId, commitSha).
/// </summary>
public sealed class BaselineStore : ISymbolStore
{
    private readonly BaselineDbFactory _factory;
    private readonly ILogger<BaselineStore> _logger;

    public BaselineStore(BaselineDbFactory factory, ILogger<BaselineStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // =========================================================================
    // Write path (T02)
    // =========================================================================

    /// <inheritdoc/>
    /// <remarks>Checks DB file existence only — an indexed project with zero symbols is still a valid baseline (ADR-018).</remarks>
    public async Task<bool> BaselineExistsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        // DB file existence is sufficient — empty projects are valid baselines
        using var conn = _factory.OpenExisting(repoId, commitSha);
        return await Task.FromResult(conn is not null);
    }

    /// <summary>
    /// Creates a baseline index from a compilation result.
    /// <paramref name="repoRootPath"/> is stored in the DB so that
    /// <see cref="GetFileSpanAsync"/> can resolve repo-relative paths to disk.
    /// </summary>
    public async Task CreateBaselineAsync(
        RepoId repoId,
        CommitSha commitSha,
        CompilationResult data,
        string repoRootPath = "",
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Creating baseline {RepoId}/{CommitSha}: {Symbols} symbols, {Refs} refs, {Files} files",
            repoId.Value, commitSha.Value[..8],
            data.Symbols.Count, data.References.Count, data.Files.Count);

        using var conn = _factory.OpenOrCreate(repoId, commitSha);
        using var tx = conn.BeginTransaction();
        try
        {
            if (!string.IsNullOrWhiteSpace(repoRootPath))
                await InsertRepoMetaAsync(conn, tx, repoRootPath, ct);

            await InsertSemanticLevelAsync(conn, tx, data.Stats.SemanticLevel, data.Stats.ProjectDiagnostics, ct);

            if (data.DllFingerprint is not null)
                await InsertDllFingerprintAsync(conn, tx, data.DllFingerprint, ct);

            // Build path→fileId lookup (files must be inserted first)
            var fileIdByPath = BuildFileIdMap(data.Files);

            await InsertFilesAsync(conn, tx, data.Files, ct);
            await InsertSymbolsAsync(conn, tx, data.Symbols, fileIdByPath, ct);
            await InsertRefsAsync(conn, tx, data.References, fileIdByPath, ct);
            if (data.TypeRelations is { Count: > 0 } typeRelations)
                await InsertTypeRelationsAsync(conn, tx, typeRelations, ct);
            if (data.Facts is { Count: > 0 } facts)
                await InsertFactsAsync(conn, tx, facts, fileIdByPath, ct);
            tx.Commit();

            // Rebuild FTS index after symbols are committed (content= tables require explicit rebuild)
            await RebuildFtsAsync(conn, ct);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ISymbolStore.CreateBaselineAsync is satisfied by the public overload above
    // (repoRootPath defaults to "" when called through the interface).

    private static Dictionary<string, string> BuildFileIdMap(IReadOnlyList<ExtractedFile> files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            map[f.Path.Value] = f.FileId;
        return map;
    }

    private static async Task InsertRepoMetaAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string repoRootPath,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO repo_meta(key, value) VALUES ('repo_root', $value)";
        cmd.Parameters.AddWithValue("$value", repoRootPath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertDllFingerprintAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string fingerprint,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO repo_meta(key, value) VALUES ('dll_fingerprint', $fp)";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertSemanticLevelAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        SemanticLevel level,
        IReadOnlyList<ProjectDiagnostic>? diagnostics,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO repo_meta(key, value) VALUES ('semantic_level', $level);
            INSERT OR REPLACE INTO repo_meta(key, value) VALUES ('project_diagnostics', $diag);
            """;
        cmd.Parameters.AddWithValue("$level", level.ToString());
        cmd.Parameters.AddWithValue("$diag",
            diagnostics is { Count: > 0 }
                ? JsonSerializer.Serialize(diagnostics)
                : "[]");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<SemanticLevel?> GetSemanticLevelAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM repo_meta WHERE key = 'semantic_level' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string s) return null;
        return Enum.TryParse<SemanticLevel>(s, out var level) ? level : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProjectDiagnostic>> GetProjectDiagnosticsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM repo_meta WHERE key = 'project_diagnostics' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string json) return [];

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<ProjectDiagnostic>>(json)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<SymbolCard?> GetSymbolByStableIdAsync(
        RepoId repoId,
        CommitSha commitSha,
        StableId stableId,
        CancellationToken ct = default)
    {
        if (stableId.IsEmpty) return null;

        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   s.namespace, s.containing_type, f.path, s.span_start, s.span_end,
                   s.visibility, s.confidence, s.stable_id, s.is_decompiled
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

    private static async Task InsertFilesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<ExtractedFile> files,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO files (file_id, path, sha256, project_id, content)
            VALUES ($file_id, $path, $sha256, $project_id, $content)
            """;

        var pFileId = cmd.Parameters.Add("$file_id", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pSha256 = cmd.Parameters.Add("$sha256", SqliteType.Text);
        var pProjectId = cmd.Parameters.Add("$project_id", SqliteType.Text);
        var pContent = cmd.Parameters.Add("$content", SqliteType.Text);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            pFileId.Value = file.FileId;
            pPath.Value = file.Path.Value;
            pSha256.Value = file.Sha256Hash;
            pProjectId.Value = (object?)file.ProjectName ?? DBNull.Value;
            pContent.Value = (object?)file.Content ?? DBNull.Value;
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
                 signature, documentation, namespace, containing_type, visibility, confidence,
                 stable_id, name_tokens)
            VALUES
                ($symbol_id, $fqname, $kind, $file_id, $span_start, $span_end,
                 $signature, $documentation, $namespace, $containing_type, $visibility, $confidence,
                 $stable_id, $name_tokens)
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
        var pStableId = cmd.Parameters.Add("$stable_id", SqliteType.Text);
        var pNameTokens = cmd.Parameters.Add("$name_tokens", SqliteType.Text);

        foreach (var s in symbols)
        {
            ct.ThrowIfCancellationRequested();

            // Skip symbols whose file is not in this compilation (e.g. framework types)
            if (!fileIdByPath.TryGetValue(s.FilePath.Value, out var fileId))
                continue;

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
            pStableId.Value = s.StableId.HasValue && !s.StableId.Value.IsEmpty
                                    ? (object)s.StableId.Value.Value : DBNull.Value;
            pNameTokens.Value = BuildNameTokens(s.FullyQualifiedName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Builds a space-separated string of lowercase CamelCase component words extracted
    /// from the simple name of the symbol. Used to populate the FTS <c>name_tokens</c>
    /// column so that searching "Service" finds "IGitService".
    /// </summary>
    internal static string BuildNameTokens(string fullyQualifiedName)
    {
        // Strip doc-comment-id prefix (M:, T:, P:, E:, F:)
        var fqn = fullyQualifiedName;
        if (fqn.Length > 2 && fqn[1] == ':')
            fqn = fqn[2..];

        // Strip parameter list FIRST (so the last-dot search doesn't land inside parameters)
        // e.g. "Ns.Class.Method(System.String)" → "Ns.Class.Method"
        var parenIdx = fqn.IndexOf('(');
        if (parenIdx >= 0) fqn = fqn[..parenIdx];

        // Strip generic arity e.g. "Ns.Class`1" or "Ns.Class.Method``1"
        var backtickIdx = fqn.IndexOf('`');
        if (backtickIdx >= 0) fqn = fqn[..backtickIdx];

        // Take the last segment after the final '.' (strip namespace and class prefix)
        var lastDot = fqn.LastIndexOf('.');
        var simpleName = lastDot >= 0 ? fqn[(lastDot + 1)..] : fqn;

        if (string.IsNullOrEmpty(simpleName)) return "";


        // Split CamelCase: "IGitService" → ["I","Git","Service"]
        // Pattern: split before each uppercase letter that follows a lowercase letter,
        // or before an uppercase letter followed by a lowercase letter (for acronyms like "HTML")
        var tokens = System.Text.RegularExpressions.Regex.Split(simpleName,
            @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant());

        return string.Join(" ", tokens);
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
            INSERT OR IGNORE INTO refs
                (from_symbol_id, to_symbol_id, ref_kind, file_id, loc_start, loc_end,
                 resolution_state, to_name, to_container_hint, stable_from_id, stable_to_id,
                 is_decompiled)
            VALUES
                ($from_symbol_id, $to_symbol_id, $ref_kind, $file_id, $loc_start, $loc_end,
                 $resolution_state, $to_name, $to_container_hint, $stable_from_id, $stable_to_id,
                 $is_decompiled)
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
        var pIsDecompiled = cmd.Parameters.Add("$is_decompiled", SqliteType.Integer);

        foreach (var r in refs)
        {
            ct.ThrowIfCancellationRequested();

            // Skip refs from files not in this compilation (e.g. framework assemblies)
            if (!fileIdByPath.TryGetValue(r.FilePath.Value, out var fileId))
                continue;

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
            pIsDecompiled.Value = r.IsDecompiled ? 1 : 0;
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

    // =========================================================================
    // Read path (T03)
    // =========================================================================

    /// <inheritdoc/>
    public async Task<SymbolCard?> GetSymbolAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   s.namespace, s.containing_type, f.path, s.span_start, s.span_end,
                   s.visibility, s.confidence, s.stable_id, s.is_decompiled
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
    /// <remarks>Uses FTS5 <c>symbols_fts</c> virtual table. FTS5 does not support bare <c>*</c> as a wildcard — use <see cref="GetSymbolsByFileAsync"/> for unfiltered queries (ADR-017).</remarks>
    public async Task<IReadOnlyList<SymbolSearchHit>> SearchSymbolsAsync(
        RepoId repoId,
        CommitSha commitSha,
        string query,
        SymbolSearchFilters? filters,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();

        // Build query — FTS5 join with optional filters
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
                DocumentationSnippet: reader.IsDBNull(4) ? null : reader.GetString(4) is { Length: > 200 } d ? d[..200] : reader.GetString(4),
                FilePath: FilePath.From(reader.GetString(5)),
                Line: reader.GetInt32(6),
                Score: reader.GetDouble(7)));
        }
        return hits;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredReference>> GetReferencesAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        RefKind? kind,
        int limit,
        CancellationToken ct = default,
        ResolutionState? resolutionState = null)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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

        if (resolutionState.HasValue)
        {
            sql.Append(" AND r.resolution_state = $resolution_state");
            cmd.Parameters.AddWithValue("$resolution_state", resolutionState.Value.ToString().ToLowerInvariant());
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
                Excerpt: null,  // Populated by query engine
                ResolutionState: resState,
                ToName: reader.IsDBNull(6) ? null : reader.GetString(6),
                ToContainerHint: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return refs;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingReferencesAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        RefKind? kind,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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
    public async Task<IReadOnlyList<StoredTypeRelation>> GetTypeRelationsAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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
    public async Task<IReadOnlyList<StoredTypeRelation>> GetDerivedTypesAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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

    /// <inheritdoc/>
    /// <remarks>
    /// Reads file content from DISK (not from the DB). Resolves the repo root path from
    /// the <c>repo_meta</c> table (stored by <see cref="CreateBaselineAsync"/>). Returns null
    /// if the file is not indexed, repo root is missing, or the file does not exist on disk.
    /// </remarks>
    public async Task<FileSpan?> GetFileSpanAsync(
        RepoId repoId,
        CommitSha commitSha,
        FilePath filePath,
        int startLine,
        int endLine,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return null;

        // Check if file is indexed and whether it is a virtual (decompiled) file
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT is_virtual, decompiled_source FROM files WHERE path = $path LIMIT 1";
        checkCmd.Parameters.AddWithValue("$path", filePath.Value);

        using (var reader = await checkCmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;

            bool isVirtual = reader.GetInt32(0) == 1;
            if (isVirtual)
            {
                string? source = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (source is null) return null;

                // Return the requested line range from the stored content; no disk read needed.
                var lines = source.Split('\n');
                int start = Math.Max(0, startLine - 1);
                int end = Math.Min(lines.Length - 1, endLine - 1);
                var content = string.Join('\n', lines[start..(end + 1)]);
                return new FileSpan(filePath, startLine, Math.Min(endLine, lines.Length),
                    lines.Length, content, false);
            }
        }

        // Resolve repo root and fall through to disk read for real files
        string repoRoot = await GetRepoRootAsync(conn, ct);
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;

        string absolutePath = Path.Combine(repoRoot, filePath.Value.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolutePath)) return null;

        return await ReadFileSpanFromDiskAsync(filePath, absolutePath, startLine, endLine, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FilePath>> GetAllFilePathsAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM files ORDER BY path";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var paths = new List<FilePath>();
        while (await reader.ReadAsync(ct))
            paths.Add(FilePath.From(reader.GetString(0)));
        return paths;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(FilePath Path, string? Content)>> GetAllFileContentsAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, content FROM files ORDER BY path";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<(FilePath, string?)>();
        while (await reader.ReadAsync(ct))
        {
            var path = FilePath.From(reader.GetString(0));
            var content = reader.IsDBNull(1) ? null : reader.GetString(1);
            result.Add((path, content));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<string?> GetRepoRootAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return null;
        var root = await GetRepoRootAsync(conn, ct);
        return string.IsNullOrWhiteSpace(root) ? null : root;
    }

    private static async Task<string> GetRepoRootAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM repo_meta WHERE key = 'repo_root' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string ?? string.Empty;
    }

    private static async Task<FileSpan> ReadFileSpanFromDiskAsync(
        FilePath filePath,
        string absolutePath,
        int startLine,
        int endLine,
        CancellationToken ct)
    {
        const int hardCapLines = 400;

        var allLines = await File.ReadAllLinesAsync(absolutePath, ct);
        int totalLines = allLines.Length;
        int actualStart = Math.Max(1, startLine);
        int actualEnd = Math.Min(totalLines, endLine);

        if (actualStart > totalLines)
            return new FileSpan(filePath, startLine, endLine, totalLines, string.Empty, false);

        var lines = allLines[(actualStart - 1)..actualEnd];
        bool truncated = false;

        if (lines.Length > hardCapLines)
        {
            lines = lines[..hardCapLines];
            truncated = true;
        }

        var content = string.Join('\n',
            lines.Select((line, i) => $"{actualStart + i,5} | {line}"));

        return new FileSpan(
            filePath,
            actualStart,
            actualStart + lines.Length - 1,
            totalLines,
            content,
            truncated);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SymbolCard>> GetSymbolsByFileAsync(
        RepoId repoId,
        CommitSha commitSha,
        FilePath filePath,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   s.namespace, s.containing_type, f.path, s.span_start, s.span_end,
                   s.visibility, s.confidence, s.stable_id, s.is_decompiled
            FROM symbols s
            JOIN files f ON s.file_id = f.file_id
            WHERE f.path = $path
            """;
        cmd.Parameters.AddWithValue("$path", filePath.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<SymbolCard>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(ReadSymbolCard(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SymbolSearchHit>> GetSymbolsByKindsAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<SymbolKind>? kinds,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder("""
            SELECT s.symbol_id, s.fqname, s.kind, s.signature, s.documentation,
                   f.path, s.span_start
            FROM symbols s
            JOIN files f ON s.file_id = f.file_id
            """);

        if (kinds is { Count: > 0 })
        {
            var placeholders = kinds.Select((_, i) => $"$k{i}").ToList();
            sql.Append($" WHERE s.kind IN ({string.Join(", ", placeholders)})");
            for (int i = 0; i < kinds.Count; i++)
                cmd.Parameters.AddWithValue($"$k{i}", kinds[i].ToString());
        }

        sql.Append(" ORDER BY s.fqname LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<SymbolSearchHit>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new SymbolSearchHit(
                SymbolId: SymbolId.From(reader.GetString(0)),
                FullyQualifiedName: reader.GetString(1),
                Kind: Enum.Parse<SymbolKind>(reader.GetString(2)),
                Signature: reader.IsDBNull(3) ? "" : reader.GetString(3),
                DocumentationSnippet: reader.IsDBNull(4) ? null : reader.GetString(4) is { Length: > 200 } d ? d[..200] : reader.GetString(4),
                FilePath: FilePath.From(reader.GetString(5)),
                Line: reader.GetInt32(6),
                Score: 1.0));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SymbolSummary>> GetAllSymbolSummariesAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol_id, stable_id, fqname, signature, visibility, kind
            FROM symbols
            ORDER BY fqname
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<SymbolSummary>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            StableId? stableId = reader.IsDBNull(1) ? null : new StableId(reader.GetString(1));
            result.Add(new SymbolSummary(
                SymbolId: SymbolId.From(reader.GetString(0)),
                StableId: stableId,
                FullyQualifiedName: reader.GetString(2),
                Signature: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Visibility: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Kind: Enum.Parse<SymbolKind>(reader.GetString(5))));
        }
        return result;
    }

    private static async Task RebuildFtsAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "INSERT INTO files_fts(files_fts) VALUES('rebuild')";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static SymbolCard ReadSymbolCard(SqliteDataReader reader)
    {
        StableId? stableId = null;
        if (reader.FieldCount > 12 && !reader.IsDBNull(12))
            stableId = new StableId(reader.GetString(12));

        int isDecompiled = reader.FieldCount > 13 ? reader.GetInt32(13) : 0;

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
        { StableId = stableId, IsDecompiled = isDecompiled };
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

    // =========================================================================
    // Facts
    // =========================================================================

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
            // Skip facts for files not in this compilation
            if (!fileIdByPath.TryGetValue(f.FilePath.Value, out var fileId))
                continue;
            // Skip facts with no valid symbol ID (e.g. throw inside anonymous/lambda — no doc comment ID)
            if (f.SymbolId == SymbolId.Empty)
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
    public async Task<IReadOnlyList<StoredFact>> GetFactsByKindAsync(
        RepoId repoId,
        CommitSha commitSha,
        FactKind kind,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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
    public async Task<IReadOnlyList<StoredFact>> GetFactsForSymbolAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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
    public async Task<IReadOnlyList<UnresolvedEdge>> GetUnresolvedEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<FilePath> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return [];

        using var conn = _factory.OpenExisting(repoId, commitSha);
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
    public async Task UpgradeEdgeAsync(
        RepoId repoId,
        CommitSha commitSha,
        EdgeUpgrade upgrade,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
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

    // =========================================================================
    // Lazy Metadata Resolution (PHASE-12-01)
    // =========================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Inserts a synthetic file row for the virtual DLL path (INSERT OR IGNORE) to satisfy
    /// the FK constraint, then inserts each stub symbol with is_decompiled=1.
    /// INSERT OR IGNORE on symbols ensures concurrent-safety and idempotency.
    /// Optionally inserts type_relations for the resolved type (base class, interfaces).
    /// </remarks>
    public async Task<int> InsertMetadataStubsAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<SymbolCard> stubs,
        IReadOnlyList<ExtractedTypeRelation>? typeRelations = null,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return 0;

        using var tx = conn.BeginTransaction();

        // Ensure virtual file rows exist for all unique decompiled paths
        var uniqueFilePaths = stubs
            .Select(s => s.FilePath.Value)
            .Where(p => p.StartsWith("decompiled/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var fileCmd = conn.CreateCommand();
        fileCmd.Transaction = tx;
        fileCmd.CommandText = """
            INSERT OR IGNORE INTO files (file_id, path, sha256, project_id)
            VALUES ($file_id, $path, '', NULL)
            """;
        var pFileId2 = fileCmd.Parameters.Add("$file_id", SqliteType.Text);
        var pPath2 = fileCmd.Parameters.Add("$path", SqliteType.Text);

        foreach (var virtualPath in uniqueFilePaths)
        {
            ct.ThrowIfCancellationRequested();
            // Use first 16 chars of SHA-256 of path as file_id (matches baseline convention)
            var pathHash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(virtualPath)));
            pFileId2.Value = pathHash[..16];
            pPath2.Value = virtualPath;
            await fileCmd.ExecuteNonQueryAsync(ct);
        }

        // Build path → fileId map from the deterministic hash already computed above.
        // file_id = SHA256(path)[..16] — no SELECT needed; avoids unbounded scan as DLL types accumulate.
        var virtualFileIds = uniqueFilePaths.ToDictionary(
            p => p,
            p => Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(p)))[..16],
            StringComparer.Ordinal);

        // Insert stubs
        using var symCmd = conn.CreateCommand();
        symCmd.Transaction = tx;
        symCmd.CommandText = """
            INSERT OR IGNORE INTO symbols
                (symbol_id, fqname, kind, file_id, span_start, span_end,
                 signature, documentation, namespace, containing_type,
                 visibility, confidence, stable_id, name_tokens, is_decompiled)
            VALUES
                ($symbol_id, $fqname, $kind, $file_id, $span_start, $span_end,
                 $signature, $documentation, $namespace, $containing_type,
                 $visibility, $confidence, $stable_id, $name_tokens, 1)
            """;

        var pSym = symCmd.Parameters.Add("$symbol_id", SqliteType.Text);
        var pFq = symCmd.Parameters.Add("$fqname", SqliteType.Text);
        var pKind = symCmd.Parameters.Add("$kind", SqliteType.Text);
        var pFile = symCmd.Parameters.Add("$file_id", SqliteType.Text);
        var pStart = symCmd.Parameters.Add("$span_start", SqliteType.Integer);
        var pEnd = symCmd.Parameters.Add("$span_end", SqliteType.Integer);
        var pSig = symCmd.Parameters.Add("$signature", SqliteType.Text);
        var pDoc = symCmd.Parameters.Add("$documentation", SqliteType.Text);
        var pNs = symCmd.Parameters.Add("$namespace", SqliteType.Text);
        var pCt = symCmd.Parameters.Add("$containing_type", SqliteType.Text);
        var pVis = symCmd.Parameters.Add("$visibility", SqliteType.Text);
        var pConf = symCmd.Parameters.Add("$confidence", SqliteType.Text);
        var pStable = symCmd.Parameters.Add("$stable_id", SqliteType.Text);
        var pTokens = symCmd.Parameters.Add("$name_tokens", SqliteType.Text);

        int inserted = 0;
        foreach (var stub in stubs)
        {
            ct.ThrowIfCancellationRequested();

            if (!virtualFileIds.TryGetValue(stub.FilePath.Value, out var fileId))
                continue;

            pSym.Value = stub.SymbolId.Value;
            pFq.Value = stub.FullyQualifiedName;
            pKind.Value = stub.Kind.ToString();
            pFile.Value = fileId;
            pStart.Value = stub.SpanStart;
            pEnd.Value = stub.SpanEnd;
            pSig.Value = (object?)stub.Signature ?? DBNull.Value;
            pDoc.Value = (object?)stub.Documentation ?? DBNull.Value;
            pNs.Value = stub.Namespace;
            pCt.Value = (object?)stub.ContainingType ?? DBNull.Value;
            pVis.Value = stub.Visibility;
            pConf.Value = stub.Confidence.ToString();
            pStable.Value = DBNull.Value;  // Metadata stubs don't get stable IDs
            pTokens.Value = BuildNameTokens(stub.FullyQualifiedName);

            // ExecuteNonQueryAsync returns the number of rows affected (0 for INSERT OR IGNORE no-op).
            inserted += await symCmd.ExecuteNonQueryAsync(ct);
        }

        // Insert type relations if provided
        if (typeRelations is { Count: > 0 })
            await InsertTypeRelationsAsync(conn, tx, typeRelations, ct);

        tx.Commit();
        return inserted;
    }

    /// <inheritdoc/>
    public async Task RebuildFtsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return;

        await RebuildFtsAsync(conn, ct);
    }

    /// <inheritdoc/>
    public async Task<string?> GetDllFingerprintAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM repo_meta WHERE key = 'dll_fingerprint' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    // === Lazy Decompiled Source (PHASE-12-02) ===

    /// <inheritdoc/>
    public async Task InsertVirtualFileAsync(
        RepoId repoId,
        CommitSha commitSha,
        string virtualPath,
        string content,
        IReadOnlyList<ExtractedReference>? decompiledRefs = null,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return;

        // Compute file_id as first 16 chars of SHA-256 of path (same convention as InsertMetadataStubsAsync)
        var pathHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(virtualPath)));
        var fileId = pathHash[..16];

        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO files (file_id, path, sha256, is_virtual, decompiled_source)
                VALUES ($file_id, $path, $sha256, 1, $content)
                """;
            cmd.Parameters.AddWithValue("$file_id", fileId);
            cmd.Parameters.AddWithValue("$path", virtualPath);
            cmd.Parameters.AddWithValue("$sha256", pathHash);
            cmd.Parameters.AddWithValue("$content", content);
            await cmd.ExecuteNonQueryAsync(ct);

            // Insert cross-DLL refs extracted from the decompiled SyntaxTree
            if (decompiledRefs is { Count: > 0 })
            {
                // Skip-if-already-present guard: if any ref from this decompiled source exists, skip
                using var checkCmd = conn.CreateCommand();
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT COUNT(*) FROM refs WHERE file_id = $file_id LIMIT 1";
                checkCmd.Parameters.AddWithValue("$file_id", fileId);
                var existing = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct));
                if (existing == 0)
                {
                    var fileIdByPath = new Dictionary<string, string> { [virtualPath] = fileId };
                    await InsertRefsAsync(conn, tx, decompiledRefs, fileIdByPath, ct);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpgradeDecompiledSymbolAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        string virtualFilePath,
        CancellationToken ct = default)
    {
        using var conn = _factory.OpenExisting(repoId, commitSha);
        if (conn is null) return;

        // Look up file_id for the virtual path
        using var lookupCmd = conn.CreateCommand();
        lookupCmd.CommandText = "SELECT file_id FROM files WHERE path = $path LIMIT 1";
        lookupCmd.Parameters.AddWithValue("$path", virtualFilePath);
        var fileId = await lookupCmd.ExecuteScalarAsync(ct) as string;
        if (fileId is null) return; // virtual file not yet inserted

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE symbols
            SET is_decompiled = 2,
                file_id = $fileId
            WHERE symbol_id = $symbolId
              AND is_decompiled = 1
            """;
        cmd.Parameters.AddWithValue("$fileId", fileId);
        cmd.Parameters.AddWithValue("$symbolId", symbolId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
