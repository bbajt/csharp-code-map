namespace CodeMap.Storage;

internal static class SchemaDefinition
{
    public const int Version = 1;

    // These must run outside any transaction (SQLite restriction)
    public static readonly string[] Pragmas =
    [
        "PRAGMA journal_mode = WAL",
        "PRAGMA synchronous = NORMAL",
        "PRAGMA foreign_keys = ON",
        "PRAGMA cache_size = -8000",       // 8 MB page cache (default ~2 MB)
        "PRAGMA mmap_size = 67108864",     // 64 MB memory-mapped I/O
        "PRAGMA temp_store = MEMORY",      // Temp tables in RAM, not disk
    ];

    // DDL statements run inside a transaction after PRAGMAs.
    // Each entry is a single statement (no semicolons needed).
    // NOTE: No trigger — FTS is rebuilt via 'INSERT INTO symbols_fts(symbols_fts) VALUES(''rebuild'')'
    // after each bulk symbol insert. This avoids the trigger-in-transaction limitation.
    public static readonly string[] DdlStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS repo_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS files (
            file_id           TEXT PRIMARY KEY,
            path              TEXT NOT NULL,
            sha256            TEXT NOT NULL,
            project_id        TEXT,
            is_virtual        INTEGER NOT NULL DEFAULT 0,
            -- 0 = real file on disk; 1 = virtual decompiled source stored in decompiled_source column
            decompiled_source TEXT,
            -- NULL for real files; C# source text for virtual decompiled files
            content           TEXT
            -- NULL for old baselines; full source text for files indexed after this column was added
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS symbols (
            symbol_id       TEXT PRIMARY KEY,
            fqname          TEXT NOT NULL,
            kind            TEXT NOT NULL,
            file_id         TEXT NOT NULL,
            span_start      INTEGER NOT NULL,
            span_end        INTEGER NOT NULL,
            signature       TEXT,
            documentation   TEXT,
            namespace       TEXT NOT NULL,
            containing_type TEXT,
            visibility      TEXT NOT NULL,
            confidence      TEXT NOT NULL,
            stable_id       TEXT,
            name_tokens     TEXT NOT NULL DEFAULT '',
            is_decompiled   INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (file_id) REFERENCES files(file_id)
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_symbols_stable ON symbols(stable_id) WHERE stable_id IS NOT NULL",
        """
        CREATE TABLE IF NOT EXISTS refs (
            from_symbol_id    TEXT NOT NULL,
            to_symbol_id      TEXT NOT NULL,
            ref_kind          TEXT NOT NULL,
            file_id           TEXT NOT NULL,
            loc_start         INTEGER NOT NULL,
            loc_end           INTEGER NOT NULL,
            resolution_state  TEXT NOT NULL DEFAULT 'resolved',
            to_name           TEXT,
            to_container_hint TEXT,
            stable_from_id    TEXT,
            stable_to_id      TEXT,
            is_decompiled     INTEGER NOT NULL DEFAULT 0,
            -- 0 = extracted from source code; 1 = extracted from a decompiled SyntaxTree (Level 2)
            FOREIGN KEY (file_id) REFERENCES files(file_id)
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_refs_to   ON refs(to_symbol_id, ref_kind)",
        "CREATE INDEX IF NOT EXISTS idx_refs_from ON refs(from_symbol_id, ref_kind)",
        """
        CREATE TABLE IF NOT EXISTS facts (
            symbol_id   TEXT NOT NULL,
            stable_id   TEXT,
            fact_kind   TEXT NOT NULL,
            value       TEXT NOT NULL,
            file_id     TEXT NOT NULL,
            loc_start   INTEGER NOT NULL,
            loc_end     INTEGER NOT NULL,
            confidence  TEXT NOT NULL,
            FOREIGN KEY (file_id)   REFERENCES files(file_id)
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_facts_symbol ON facts(symbol_id)",
        "CREATE INDEX IF NOT EXISTS idx_facts_kind   ON facts(fact_kind)",
        "CREATE INDEX IF NOT EXISTS idx_facts_stable ON facts(stable_id) WHERE stable_id IS NOT NULL",
        """
        CREATE TABLE IF NOT EXISTS type_relations (
            type_symbol_id    TEXT NOT NULL,
            related_symbol_id TEXT NOT NULL,
            relation_kind     TEXT NOT NULL,
            display_name      TEXT NOT NULL,
            stable_type_id    TEXT,
            stable_related_id TEXT,
            PRIMARY KEY (type_symbol_id, related_symbol_id)
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_type_rel_related ON type_relations(related_symbol_id, relation_kind)",
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
            fqname,
            signature,
            documentation,
            name_tokens,
            content=symbols,
            content_rowid=rowid
        )
        """,
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
            content,
            content='files',
            content_rowid='rowid'
        )
        """,
        "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)",
        "INSERT OR IGNORE INTO schema_version(version) VALUES (1)",
    ];
}
