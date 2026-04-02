# SYSTEM-ARCHITECTURE.MD

## CodeMap — Roslyn-Powered Semantic Code Index & MCP Service

---

# 1. Purpose

CodeMap is a **semantic, token-efficient code browsing backend** for AI agents
working on large C# / .NET repositories.

The goal is to:

* Eliminate brute-force file scanning by AI agents
* Avoid context window bloat (80–95% token reduction vs. raw file reading)
* Maintain correctness under active edits via workspace overlays
* Support multi-agent and multi-branch workflows with isolation
* Provide fast, structured, evidence-based responses

The architecture is optimized for:

* Local-first development (no network required)
* Incremental indexing via Roslyn semantic analysis
* Workspace isolation for concurrent agents
* High read performance (sub-30ms symbol search)
* Controlled API budgets to prevent over-fetching

---

# 2. Technology Stack

| Component         | Technology                                    | Notes                           |
| ----------------- | --------------------------------------------- | ------------------------------- |
| Language          | C# 12 / .NET 9                               | `<LangVersion>12</LangVersion>` |
| Semantic engine   | Microsoft.CodeAnalysis (Roslyn) 4.x           | MSBuildWorkspace                |
| Git integration   | LibGit2Sharp                                  | Or CLI fallback                 |
| Storage           | SQLite (WAL mode) via Microsoft.Data.Sqlite   | One DB per baseline commit      |
| MCP transport     | Stdio (primary), HTTP/SSE (optional)          | Model Context Protocol          |
| DI / Hosting      | Microsoft.Extensions.Hosting                  | Generic host for daemon         |
| Logging           | Microsoft.Extensions.Logging                  | Structured, ILogger<T>          |
| Testing           | xUnit + FluentAssertions + Verify             | Snapshot testing for cards      |
| Benchmarking      | BenchmarkDotNet                               | p95 regression tracking         |
| Build             | dotnet CLI, Directory.Build.props             | Central package management      |
| Packaging         | .NET global tool + self-contained binaries    | win/linux/mac                   |
| CI                | GitHub Actions                                | Test + benchmark on PR          |

---

# 3. High-Level Architecture

```
AI Agents (Claude Code, VS Code Copilot, Cursor, etc.)
   │
   ▼
┌──────────────────────────────────────┐
│  codemap-mcp  (MCP Façade CLI)       │  Thin: validation, budgets, routing
│  Project: CodeMap.Mcp                │  Stdio transport (primary)
└──────────────────┬───────────────────┘
                   │
                   ▼
┌──────────────────────────────────────┐
│  codemapd  (Index Daemon / Engine)   │  Core semantic engine
│  Project: CodeMap.Daemon             │  Composition root (DI wiring)
│  ┌────────────────────────────────┐  │
│  │ CodeMap.Git                    │  │  Repo identity, commit, diff
│  │ CodeMap.Roslyn                 │  │  Compilation, extraction
│  │ CodeMap.Storage                │  │  SQLite baseline + overlay
│  │ CodeMap.Query                  │  │  Search, merge, rank, cache
│  └────────────────────────────────┘  │
└──────────────────────────────────────┘

Shared foundation: CodeMap.Core (domain types, interfaces, zero dependencies)
```

Optional (Milestone 03+):

```
Supervisor / Orchestrator (multi-agent branch management)
Shared Baseline Cache (org-wide, pull/push per commit SHA)
```

---

# 4. Solution Structure

```
/src
  CodeMap.Core/                  ← Domain types, interfaces, no dependencies
  CodeMap.Git/                   ← Git integration (LibGit2Sharp)
  CodeMap.Roslyn/                ← Roslyn compilation + extraction
  CodeMap.Storage/               ← SQLite baseline + overlay
  CodeMap.Query/                 ← Query engine, merge, rank, cache
  CodeMap.Mcp/                   ← MCP façade (stdio transport)
  CodeMap.Daemon/                ← Host process for index daemon
/tests
  CodeMap.Core.Tests/
  CodeMap.Git.Tests/
  CodeMap.Roslyn.Tests/
  CodeMap.Storage.Tests/
  CodeMap.Query.Tests/
  CodeMap.Mcp.Tests/
  CodeMap.Integration.Tests/     ← End-to-end: MCP → Engine → DB
  CodeMap.Benchmarks/            ← BenchmarkDotNet performance suite
/testdata
  SampleSolution/                ← Minimal .NET solution for testing
  LargeSolution/                 ← Stress test solution (generated)
/docs
  MILESTONE.MD
  PHASE-*.MD
  SYSTEM-ARCHITECTURE.MD
  API-SCHEMA.MD
  DECISIONS.MD
```

### Dependency Rules (Strict — Enforced via Project References)

```
CodeMap.Core         → (none)
CodeMap.Git          → CodeMap.Core
CodeMap.Roslyn       → CodeMap.Core
CodeMap.Storage      → CodeMap.Core
CodeMap.Query        → CodeMap.Core, CodeMap.Storage
CodeMap.Mcp          → CodeMap.Core, CodeMap.Query
CodeMap.Daemon       → All (composition root only)
```

Any violation of this graph is a build error. No project may reference a peer
that is not in its declared dependency set. `CodeMap.Roslyn` does NOT depend on
`CodeMap.Storage` — the daemon wires them together.

---

# 5. System Components

---

## 5.1 CodeMap.Core — Domain Foundation

### Responsibility

Define all shared types, interfaces, and contracts. Zero external dependencies.

### Key Types

**Identifiers** (strongly-typed wrappers):
* `RepoId` — string, from remote URL hash or path hash
* `WorkspaceId` — string, agent-assigned
* `CommitSha` — string, 40-char hex
* `SymbolId` — string, fully-qualified Roslyn symbol ID
* `FilePath` — string, repo-relative path

**Enums:**
* `ConsistencyMode` — committed | workspace | ephemeral
* `Confidence` — high | medium | low
* `SymbolKind` — class | struct | interface | enum | delegate | method | property | field | event | constant
* `RefKind` — call | read | write | instantiate | override | implementation
* `FactKind` — route | config | db_table | di_registration | middleware | exception | log | retry_policy

**Records:**
* `EvidencePointer` — repo_id, file_path, line_start, line_end, symbol_id?, excerpt?
* `SymbolCard` — see Section 8.2
* `ResponseEnvelope<T>` — see API-SCHEMA.MD Section 2.5
* `CodeMapError` — code, message, details, retryable
* `BudgetLimits` — max_results, max_references, max_depth, max_lines, max_chars

**Result Pattern:**
* `Result<T, CodeMapError>` — all operations that can fail return this type
* No exceptions for expected failures (NOT_FOUND, BUDGET_EXCEEDED, etc.)
* Exceptions reserved for truly exceptional conditions (OOM, disk failure)

**Interfaces:**
* `IGitService` — repo identity, commit detection, changed files
* `IRoslynCompiler` — solution loading, compilation, symbol extraction
* `ISymbolStore` — baseline read/write, overlay read/write
* `IQueryEngine` — search, get_card, get_span, refs, graph traversal
* `ICacheService` — L1 in-memory cache get/set/invalidate

### Design Rules

* All async public methods accept `CancellationToken`
* Nullable reference types enabled, zero warnings policy
* All DTOs are `record` or `record struct`

---

## 5.2 CodeMap.Git — Git Integration Layer

### Responsibility

* Provide repository identity and state
* Detect current commit SHA and branch
* Detect working tree changes (modified/added/deleted files)
* Detect checkout, rebase, and merge events

### Key Operations

* `GetRepoIdentity()` → `RepoId` (from remote URL or path hash)
* `GetCurrentCommit()` → `CommitSha`
* `GetCurrentBranch()` → `string`
* `GetChangedFiles(CommitSha baseline)` → `IReadOnlyList<FileChange>`
* `IsClean()` → `bool`

### Implementation

* Primary: LibGit2Sharp (in-process, fast)
* Fallback: `git` CLI via `Process.Start` (if LibGit2Sharp fails on exotic repos)

### Design Rules

* Baseline index is always keyed by `CommitSha`
* Git is the sole authority for baseline immutability
* No write operations (commits, checkouts) — read-only

---

## 5.3 CodeMap.Roslyn — Semantic Engine

### Responsibility

* Load .NET solutions via `MSBuildWorkspace`
* Compile all projects (incremental when possible)
* Extract symbols with full semantic metadata
* Extract references with classification
* Produce structured `SymbolCard` records
* Fall back to syntax-only extraction if compilation fails

### Compilation Strategy

1. Load solution via `MSBuildWorkspace.Create()`
2. Compile all projects (or only affected projects for incremental)
3. If compilation fails with errors, fall back to syntactic extraction with
   `Confidence.Low` on affected files
4. Report compilation diagnostics in response metadata

### Symbol Extraction

Walk all `INamedTypeSymbol` and `IMethodSymbol` (etc.) from each compilation:

* Fully qualified name (Roslyn `ToDisplayString`)
* Kind (`SymbolKind` enum)
* Signature (return type + parameters)
* XML documentation summary (from `///` comments)
* Containing namespace and type
* File location (path, span start/end)
* Visibility (public, internal, protected, private)
* Content hash (for change detection)

### Reference Classification

For each `SyntaxNode` that references a symbol, classify as:

| RefKind        | Detection                                         |
| -------------- | ------------------------------------------------- |
| call           | `InvocationExpression`, `ObjectCreationExpression` |
| read           | `IdentifierName` in read context                  |
| write          | `IdentifierName` in assignment LHS                |
| instantiate    | `ObjectCreationExpression` specifically            |
| override       | Method with `override` modifier                   |
| implementation | Method implementing interface member               |

### Handles

* Partial classes → unified via Roslyn semantic model
* Generics → full type parameter + constraint resolution
* async/await → `Task<T>` return type understanding
* Extension methods → `this` parameter detection
* Attributes → extracted as facts (see Fact Extraction)
* Cross-project references → resolved via solution-wide compilation

### Syntactic Fallback

When compilation fails (missing dependencies, SDK issues):

1. Parse files with `CSharpSyntaxTree.ParseText()`
2. Extract symbols from syntax nodes only (no type resolution)
3. Mark all extracted data with `Confidence.Low`
4. Log which projects failed and why

---

## 5.4 CodeMap.Storage — SQLite Persistence

### Responsibility

* Store and retrieve baseline indexes (immutable per commit)
* Store and retrieve overlay indexes (mutable per workspace)
* Provide FTS5 full-text search
* Manage database lifecycle (create, migrate, vacuum)

### Storage Model

**Baseline DB:**
* SQLite, WAL mode
* One database file per `(repo_id, commit_sha)`
* Path: `~/.codemap/baselines/{repo_id}/{commit_sha}.db`
* Immutable after initial population

**Overlay DB:** (Milestone 02)
* SQLite, WAL mode
* One database file per `(repo_id, commit_sha, workspace_id)`
* Path: `~/.codemap/overlays/{repo_id}/{workspace_id}.db`
* Write-optimized, revisioned (MVCC-like)

### Core Schema

**symbols**
```sql
CREATE TABLE symbols (
    symbol_id   TEXT PRIMARY KEY,
    fqname      TEXT NOT NULL,
    kind        TEXT NOT NULL,       -- SymbolKind enum value
    file_id     TEXT NOT NULL,
    span_start  INTEGER NOT NULL,
    span_end    INTEGER NOT NULL,
    signature   TEXT,
    documentation TEXT,
    visibility  TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    FOREIGN KEY (file_id) REFERENCES files(file_id)
);
```

**refs**
```sql
CREATE TABLE refs (
    from_symbol_id TEXT NOT NULL,
    to_symbol_id   TEXT NOT NULL,
    ref_kind       TEXT NOT NULL,    -- RefKind enum value
    file_id        TEXT NOT NULL,
    loc_start      INTEGER NOT NULL,
    loc_end        INTEGER NOT NULL,
    FOREIGN KEY (from_symbol_id) REFERENCES symbols(symbol_id),
    FOREIGN KEY (to_symbol_id)   REFERENCES symbols(symbol_id),
    FOREIGN KEY (file_id)        REFERENCES files(file_id)
);
CREATE INDEX idx_refs_to   ON refs(to_symbol_id, ref_kind);
CREATE INDEX idx_refs_from ON refs(from_symbol_id, ref_kind);
```

**files**
```sql
CREATE TABLE files (
    file_id           TEXT PRIMARY KEY,
    path              TEXT NOT NULL,
    sha256            TEXT NOT NULL,
    project_id        TEXT,
    is_virtual        INTEGER NOT NULL DEFAULT 0,
    -- 0 = real source file; 1 = virtual decompiled source (stored in decompiled_source)
    decompiled_source TEXT,
    content           TEXT
    -- NULL for old baselines; full source text for files indexed after commit 7f54adb
);
```

**facts**
```sql
CREATE TABLE facts (
    symbol_id   TEXT NOT NULL,
    fact_kind   TEXT NOT NULL,       -- FactKind enum value
    value       TEXT NOT NULL,
    file_id     TEXT NOT NULL,
    loc_start   INTEGER NOT NULL,
    loc_end     INTEGER NOT NULL,
    confidence  TEXT NOT NULL,       -- Confidence enum value
    FOREIGN KEY (symbol_id) REFERENCES symbols(symbol_id),
    FOREIGN KEY (file_id)   REFERENCES files(file_id)
);
CREATE INDEX idx_facts_symbol ON facts(symbol_id);
CREATE INDEX idx_facts_kind   ON facts(fact_kind);
```

**FTS5 symbol index**
```sql
CREATE VIRTUAL TABLE symbols_fts USING fts5(
    fqname,
    signature,
    documentation,
    name_tokens,
    content=symbols,
    content_rowid=rowid
);
```

**FTS5 file content index**
```sql
CREATE VIRTUAL TABLE files_fts USING fts5(
    content,
    content='files',
    content_rowid='rowid'
);
```
Used by `code.search_text` for candidate file pre-filtering. Both FTS5 tables are
external content tables — rebuilt explicitly via
`INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')` and
`INSERT INTO files_fts(files_fts) VALUES('rebuild')` in `BaselineStore.RebuildFtsAsync`
after each bulk insert. No triggers (SQLite limitation).

### Overlay Revision Model (Milestone 02)

Every overlay update increments:
```
overlay_revision++
```
All cache keys must include the revision. Overlay rows override baseline rows
by `symbol_id` match during query-time merge.

---

## 5.5 CodeMap.Query — Query Engine

### Responsibility

* Execute symbol searches (FTS + filters)
* Produce SymbolCard responses
* Retrieve bounded file spans
* Find references with classification (Milestone 02)
* Traverse call graphs (Milestone 02)
* Merge baseline + overlay results (Milestone 02)
* Enforce budgets
* Manage L1 cache

### Query Execution Model

**Milestone 01 (baseline-only):**
1. Check L1 cache
2. Query baseline DB
3. Rank results
4. Enforce budget limits
5. Cache result
6. Return in envelope

**Milestone 02+ (with overlays):**
1. Check L1 cache
2. Query overlay DB (if workspace consistency)
3. Query baseline DB
4. Merge (overlay wins by `symbol_id`)
5. Rank results
6. Enforce budget limits
7. Cache result
8. Return in envelope

### Supported Queries

**Milestone 01:**
* `symbols.search(query, filters?, limit?)` — FTS search with ranking
* `symbols.get_card(symbol_id)` — structured SymbolCard
* `code.get_span(file_path, start_line, end_line, context_lines?)` — bounded excerpt
* `symbols.get_definition_span(symbol_id, max_lines?)` — convenience wrapper

**Milestone 02:**
* `refs.find(symbol_id, ref_kind?, limit?)` — classified references
* `graph.callers(symbol_id, depth?, limit?)` — depth-limited callers
* `graph.callees(symbol_id, depth?, limit?)` — depth-limited callees
* `types.hierarchy(symbol_id)` — base, interfaces, derived

**Milestone 03:**
* `surfaces.list_endpoints(filter?)` — ASP.NET routes
* `surfaces.list_config_keys(filter?)` — IConfiguration usage
* `surfaces.list_db_tables(filter?)` — EF entities, raw SQL strings

### Consistency Modes

| Mode       | Sources              | Workspace Required | Virtual Files |
| ---------- | -------------------- | ------------------ | ------------- |
| committed  | Baseline only        | No                 | No            |
| workspace  | Baseline + Overlay   | Yes                | No            |
| ephemeral  | Baseline + Overlay + Virtual | Yes         | Yes           |

---

## 5.6 CodeMap.Mcp — MCP Server Façade

### Responsibility

* Register MCP tools (stdio transport)
* Validate input schemas
* Enforce budget limits
* Route requests via `repo_id` + `workspace_id`
* Format responses into `ResponseEnvelope<T>`
* Authentication (optional, future)

### Budget Enforcement

| Budget          | Default | Hard Cap |
| --------------- | ------- | -------- |
| max_results     | 20      | 100      |
| max_references  | 50      | 500      |
| max_depth       | 3       | 6        |
| max_lines       | 120     | 400      |
| max_chars       | 12,000  | 40,000   |

Requests exceeding hard caps are rejected with `BUDGET_EXCEEDED`.
Requests within default–hard cap range are honored but flagged in `limits_applied`.

### MCP must NOT contain:
* Indexing logic
* Roslyn references
* Direct SQLite access
* Business rules beyond validation

---

## 5.7 CodeMap.Daemon — Host Process

### Responsibility

* Composition root (DI wiring of all components)
* Host process lifecycle (start, shutdown, signal handling)
* Configuration loading from `~/.codemap/config.json`
* Logging pipeline setup

### Process Model

```
codemap-mcp (CLI)
   │ stdio
   ▼
CodeMap.Mcp (tool dispatch)
   │ in-process calls
   ▼
CodeMap.Query → CodeMap.Storage → SQLite
                CodeMap.Roslyn → MSBuildWorkspace
                CodeMap.Git → LibGit2Sharp
```

Single-process for Milestone 01. Daemon separation (codemapd) is optional
and targeted for Milestone 04 if needed for background indexing.

---

# 6. Indexing Model

---

## 6.1 Baseline Index

**Scope:** Immutable per `(repo_id, commit_sha)`

**Contains:**
* All symbols (classes, methods, properties, fields, events, etc.)
* All references (classified by RefKind)
* File metadata (path, hash, project)
* Extracted facts (routes, config, DB tables — Milestone 03)
* FTS5 full-text index

**Characteristics:**
* Read-optimized (indexed, WAL mode)
* Shared across all workspaces for the same commit
* Immutable — never modified after creation
* Keyed by commit SHA — branch name is irrelevant

---

## 6.2 Overlay Index (Milestone 02)

**Scope:** Mutable per `(repo_id, commit_sha, workspace_id)`

**Contains:** Only changed files and their affected symbols.

**Behavior:**
* Incremental — only recompiles affected projects
* Revisioned — every update increments `overlay_revision`
* Override — overlay rows replace baseline rows by `symbol_id`

**MVCC Revision Model:**
```
overlay_revision = 0  (initial)
agent edits file A   → reindex A → overlay_revision = 1
agent edits file B   → reindex B → overlay_revision = 2
```

All cache keys include `overlay_revision` to ensure consistency.

---

## 6.3 Ephemeral Query Overlay (Milestone 02)

Per-request override for unsaved edits:
* Agent passes `virtual_files[]` in request
* Engine compiles with virtual content replacing actual files
* Results apply only to current query
* No persistent state change

---

# 7. Hot Cache Layer

## 7.1 L1 In-Memory Cache

Cache key components:
```
(repo_id, commit_sha, workspace_id?, overlay_revision, query_signature)
```

Cached data:
* Symbol cards
* Search results
* File spans
* Caller/callee expansions (Milestone 02)

## 7.2 Invalidation Strategy

* Overlay revision increment invalidates all workspace-scoped keys
* Baseline cache entries are permanent (immutable baseline)
* Cache size bounded by configurable entry count + LRU eviction
* Manual invalidation via `index.refresh_overlay` (Milestone 02)

---

# 8. SymbolCard Structure

## 8.1 Purpose

The SymbolCard is the primary unit of semantic information returned to agents.
It replaces reading 50–200 lines of source code with a structured summary that
contains everything an agent needs for reasoning.

## 8.2 Fields

```
SymbolCard {
    symbol_id:        SymbolId        // Fully-qualified Roslyn symbol ID
    fqname:           string          // Human-readable fully-qualified name
    kind:             SymbolKind      // class, method, property, etc.
    signature:        string          // Return type + parameters
    documentation:    string?         // XML doc <summary> content
    namespace:        string          // Containing namespace
    containing_type:  string?         // Containing type (null for top-level)
    file_path:        FilePath        // Repo-relative file path
    span:             { start, end }  // Line numbers
    visibility:       string          // public, internal, protected, private
    calls_top:        SymbolRef[]     // Top N called symbols (by frequency)
    facts:            Fact[]          // Extracted facts (routes, DI, etc.)
    side_effects:     string[]        // Heuristic: DB writes, HTTP calls, etc.
    thrown_exceptions: string[]       // Heuristic: throw statements
    evidence:         EvidencePointer[]  // Source location pointers
    confidence:       Confidence      // high (compiled) | low (syntax-only)
}
```

## 8.3 Token Efficiency

A typical SymbolCard is 200–500 tokens. The equivalent raw source code for the
same symbol averages 2,000–8,000 tokens. This yields a **4–16x token reduction**
per symbol lookup.

---

# 9. Fact Extraction (Milestone 03)

## 9.1 Purpose

Facts are structured metadata extracted from code that describes architectural
behavior. An agent asking "what endpoints does this service expose?" gets a
direct answer instead of scanning every controller.

## 9.2 Supported Fact Kinds

| FactKind          | Source                                        | Example Value                      |
| ----------------- | --------------------------------------------- | ---------------------------------- |
| route             | `[HttpGet]`, `[Route]`, `MapGet()` calls     | `GET /api/orders/{id}`             |
| config            | `IConfiguration["key"]`, `[ConfigSection]`   | `ConnectionStrings:DefaultDB`      |
| db_table          | EF `DbSet<T>`, raw SQL strings               | `dbo.Orders`                       |
| di_registration   | `AddScoped<T>()`, `AddSingleton<T>()`        | `IOrderService → OrderService (Scoped)` |
| middleware        | `app.UseAuthentication()`, pipeline order     | `AuthenticationMiddleware (pos: 3)` |
| exception         | `throw new` statements                       | `OrderNotFoundException`           |
| log               | `_logger.LogWarning(...)` patterns            | `"Order {Id} not found" (Warning)` |
| retry_policy      | Polly policies, `AddResilienceHandler()`     | `Retry 3x, backoff exponential`    |

## 9.3 Confidence

Facts derived from attributes and explicit API calls → `Confidence.High`
Facts derived from heuristic string matching → `Confidence.Medium`
Facts derived from naming conventions only → `Confidence.Low`

---

# 10. Multi-Agent & Branch Strategy (Milestone 02–03)

---

## 10.1 Isolation Model

Each agent receives:
* `baseline_commit_sha` — shared, immutable
* `workspace_id` — agent-specific, isolated
* Optional path scope (restrict to certain directories)

Overlays are fully isolated. Agent A's edits are invisible to Agent B.

## 10.2 Branch Handling

When branch changes (checkout, rebase, merge):
1. New `commit_sha` detected by `CodeMap.Git`
2. Check if baseline index exists for new commit
3. If not, build new baseline (or pull from shared cache)
4. Reset or migrate overlays as appropriate

## 10.3 Supervisor Flow (Milestone 03)

1. Supervisor spawns sub-agent with `(branch, workspace_id)`
2. Agent edits files, overlay updates incrementally
3. Agent commits changes
4. Supervisor merges branch
5. New baseline index built for merge commit
6. Other workspaces updated or notified

---

# 11. Packaging & Deployment

---

## 11.1 Local Installation (Primary)

**As .NET global tool:**
```bash
dotnet tool install -g codemap-mcp
```

**As self-contained binary:**
* `codemap-mcp-win-x64.exe`
* `codemap-mcp-linux-x64`
* `codemap-mcp-osx-arm64`

**Configuration directory:** `~/.codemap/`
```
~/.codemap/
  config.json              ← Settings (budget overrides, log level)
  baselines/{repo_id}/     ← Baseline DBs per commit
  overlays/{repo_id}/      ← Overlay DBs per workspace
  logs/                    ← Structured log files
  _savings.json            ← Running token savings counter
```

## 11.2 Docker Deployment (Milestone 04)

Single container running codemap-mcp:
```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:9.0
COPY publish/ /app/
ENTRYPOINT ["/app/codemap-mcp"]
```

Mount points:
* `/repo` — source repository (read-only)
* `/cache` — baseline + overlay databases (persistent)

Best for: CI pipelines, shared index servers, reproducible builds.

## 11.3 Shared Baseline Cache (Milestone 03)

* Stores baseline DB files per `(repo_id, commit_sha)`
* Clients pull missing indexes before building locally
* Overlays always remain local
* Protocol: simple file copy (rsync, S3, or network share)

---

# 12. Performance Targets

| Operation                           | p95 Target |
| ----------------------------------- | ---------- |
| `symbols.search` (FTS, limit=20)   | < 30 ms    |
| `symbols.get_card`                 | < 10 ms    |
| `refs.find` (limit=50)            | < 80 ms    |
| `graph.callers` (depth=2)         | < 150 ms   |
| Incremental re-index (single file) | < 200 ms   |
| Baseline full index (100-file sln) | < 30 s     |

---

# 13. Observability

Every `ResponseEnvelope<T>` includes a `meta` block:

```json
{
  "meta": {
    "baseline_commit_sha": "abc123...",
    "workspace_id": null,
    "overlay_revision": 0,
    "timing_ms": {
      "total": 12,
      "cache_lookup": 1,
      "db_query": 8,
      "roslyn_compile": 0,
      "ranking": 3
    },
    "limits_applied": {
      "max_results": { "requested": 50, "applied": 20 }
    },
    "cache_hit": false,
    "tokens_saved": 4200,
    "cost_avoided": {
      "claude_sonnet": 0.013
    },
    "tokens_saved_total": 128000,
    "cost_avoided_total": {
      "claude_sonnet": 0.384
    }
  }
}
```

* `tokens_saved` — estimated tokens saved for this query vs. raw file reading
* `cost_avoided` — estimated cost at standard model pricing
* `*_total` — running session totals, persisted to `~/.codemap/_savings.json`

---

# 14. Safety & Correctness Principles

* **Evidence-first responses** — every claim has an `EvidencePointer`
* **Explicit confidence flags** — `high` (compiled), `medium` (heuristic), `low` (syntax-only)
* **Result pattern** — `Result<T, CodeMapError>` for all fallible operations; no exceptions for expected failures
* **Strict query budgets** — hard caps prevent runaway queries
* **Immutable baselines** — keyed by commit SHA, never modified
* **Revision-based invalidation** — overlay changes auto-invalidate stale cache
* **Path traversal prevention** — all file paths validated against repo root
* **Binary exclusion** — binary files skipped during indexing
* **`.gitignore` respect** — ignored files are never indexed
* **No secrets in storage** — index DBs contain only structural metadata, not secrets

---

# 15. Coding Standards

* **Nullable reference types:** `<Nullable>enable</Nullable>` globally, zero warnings
* **Records for DTOs:** all request/response types are `record` or `record struct`
* **Result pattern:** `Result<T, CodeMapError>` — no exceptions for expected failures
* **CancellationToken:** every async public method accepts `CancellationToken ct`
* **Logging:** `ILogger<T>` via `Microsoft.Extensions.Logging`, structured only
* **No static mutable state:** all state flows through DI
* **Interface segregation:** each component boundary is an interface in `CodeMap.Core`
* **Test naming:** `MethodName_Scenario_ExpectedResult`
* **Test tagging:** `[Trait("Category", "Integration")]` for integration tests
* **Snapshot testing:** Verify library for complex output assertions

---

# 16. Phased Implementation Plan

The implementation is organized into milestones and phases. See **MILESTONE.MD**
for the definitive plan with phase-level detail, task breakdowns, and dependencies.

**Summary:**

| Milestone | Goal                                    | Key Tools                          |
| --------- | --------------------------------------- | ---------------------------------- |
| 01        | Foundation — baseline index + search    | search, get_card, get_span         |
| 02        | Workspace — overlays + navigation       | refs.find, graph.*, types.*        |
| 03        | Surfaces — extractors + multi-agent     | surfaces.*, supervisor flow        |
| 04        | Performance — tuning + packaging        | Docker, global tool, benchmarks    |

---

# 17. Future Extensions

* Multi-language plugins (TypeScript, Go, Java via their respective compilers)
* Static analysis enhancements (null flow, dispose tracking)
* Security scanning extractors (SQL injection patterns, secret leaks)
* Cross-repo indexing (solution references across repositories)
* Semantic diff queries ("what changed between two commits, semantically?")
* Distributed baseline index registry (pull baselines from CI artifacts)
* IDE integration (VS Code extension that hosts codemap-mcp in-process)

---

# End of Document
