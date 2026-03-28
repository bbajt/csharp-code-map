# API-SCHEMA.MD

## CodeMap — MCP Tool Surface & Backend API Contract

### Companion to SYSTEM-ARCHITECTURE.MD and MILESTONE.MD

This document defines the **complete public MCP tool surface and backend
API contract** for CodeMap.

> **Note on sync state:** This document was significantly updated in PHASE-05-05
> to match the actual C# implementation after 31 phases of drift. Sections marked
> `[NOT IMPLEMENTED]` describe spec features that were deferred or superseded.
> Trust the code over the spec if you see any remaining discrepancy.

---

# 1. API Layers

## 1.1 MCP Façade (CodeMap.Mcp)

Thin validation + routing layer:
- Enforces budget limits (see Section 3)
- Validates request schemas against this document
- Routes via `repo_id` + `workspace_id`
- Returns structured `ResponseEnvelope<T>` (see Section 2.5)
- MCP transport: stdio (primary) — HTTP/SSE optional/future

## 1.2 Backend Engine (CodeMap.Query → CodeMap.Storage → CodeMap.Roslyn)

Core semantic engine. MCP Façade must NOT contain:
- Indexing logic
- Roslyn references
- Direct SQLite access
- Business rules beyond input validation

---

# 2. Common Types

All types in this section are defined in `CodeMap.Core` and shared across all
projects.

## 2.1 Identifiers

All identifiers are strongly-typed `readonly record struct` wrappers around `string`.

```csharp
public readonly record struct RepoId(string Value);
public readonly record struct WorkspaceId(string Value);
public readonly record struct CommitSha(string Value);     // 40-char lowercase hex
public readonly record struct SymbolId(string Value);      // FQN documentation comment ID
public readonly record struct FilePath(string Value);      // Repo-relative, forward-slash
public readonly record struct StableId(string Value);      // sym_ + 16 hex chars (SSID)
```

<!-- Updated PHASE-05-05: added StableId; CommitSha constraint clarified to lowercase -->

---

## 2.2 Enums

### ConsistencyMode

```csharp
public enum ConsistencyMode
{
    Committed,   // Baseline only — no workspace required
    Workspace,   // Baseline + Overlay — workspace_id required
    Ephemeral    // Baseline + Overlay + Virtual files — workspace_id + virtual_files required
}
```

### Confidence

```csharp
public enum Confidence
{
    High,    // Full Roslyn compilation succeeded
    Medium,  // Heuristic extraction (pattern matching, regex)
    Low      // Syntax-only fallback (compilation failed)
}
```

### SymbolKind

```csharp
public enum SymbolKind
{
    Class, Struct, Interface, Enum, Delegate, Record,
    Method, Property, Field, Event, Constant, Constructor, Indexer, Operator
}
```

### RefKind

```csharp
public enum RefKind
{
    Call,           // Method invocation or delegate invocation
    Read,           // Value read (identifier in read context)
    Write,          // Value write (assignment LHS)
    Instantiate,    // Object creation (new T())
    Override,       // Method override declaration
    Implementation  // Interface member implementation
}
```

### FactKind

```csharp
public enum FactKind
{
    Route,           // HTTP endpoint route (ASP.NET attribute or minimal API)
    Config,          // Configuration key usage (IConfiguration, IOptions)
    DbTable,         // Database table / EF Core entity
    DiRegistration,  // Dependency injection registration
    Middleware,      // Middleware pipeline entry (Use*, Map*)
    Exception,       // Thrown exception type — throw new, re-throw, throw expression (PHASE-06-01)
    Log,             // Structured log message template + severity level (PHASE-06-01)
    RetryPolicy      // Resilience / retry policy (Polly, Microsoft.Extensions.Resilience)
}
```

<!-- Updated PHASE-06-01: Exception and Log extractors implemented — NOT IMPLEMENTED markers removed -->

### SemanticLevel

```csharp
// Added in PHASE-02-08 (ADR-020)
public enum SemanticLevel
{
    Full,       // All projects compiled successfully
    Partial,    // Some projects compiled, some fell back to syntax-only
    SyntaxOnly  // No projects compiled — all extraction is syntactic
}
```

<!-- Updated PHASE-05-05: added SemanticLevel enum (was missing from spec) -->

### ResolutionState

```csharp
// Added in PHASE-04-05 (ADR-022)
public enum ResolutionState
{
    Resolved,   // to_symbol_id is populated; full FQN reference available
    Unresolved  // to_symbol_id is empty; only to_name + to_container_hint available
}
```

<!-- Updated PHASE-05-05: added ResolutionState enum (was missing from spec) -->

---

## 2.3 EvidencePointer

```csharp
public record EvidencePointer(
    RepoId RepoId,
    FilePath FilePath,
    int LineStart,        // >= 1
    int LineEnd,          // >= LineStart
    SymbolId? SymbolId = null,
    string? Excerpt = null
);
```

---

## 2.4 SymbolCard

The primary unit of semantic information returned to agents.

```csharp
public record SymbolCard(
    SymbolId SymbolId,
    string FullyQualifiedName,
    SymbolKind Kind,
    string Signature,
    string? Documentation,
    string Namespace,
    string? ContainingType,
    FilePath FilePath,
    int SpanStart,             // 1-based line number
    int SpanEnd,               // 1-based line number
    string Visibility,         // "public", "internal", "private", etc.
    IReadOnlyList<SymbolRef> CallsTop,        // Outgoing calls (NOT caller list)
    IReadOnlyList<Fact> Facts,                // Populated by GetSymbolCardAsync only
    IReadOnlyList<string> SideEffects,        // Reserved — always empty currently
    IReadOnlyList<string> ThrownExceptions,   // Reserved — always empty currently
    IReadOnlyList<EvidencePointer> Evidence,  // Reserved — always empty currently
    Confidence Confidence,
    StableId? StableId = null   // Nullable: absent on baselines before PHASE-03-01
);
```

<!-- Updated PHASE-05-05: added StableId field; noted Facts only hydrated on get_card (not search);
     noted SideEffects/ThrownExceptions/Evidence are reserved/empty; CallsTop clarified as outgoing -->

### Fact

```csharp
// Simplified fact attached to SymbolCard — full details available via surfaces.* tools
public record Fact(
    FactKind Kind,
    string Value,      // "display|metadata" format; pipe-separated where applicable
    string? Detail = null
);
```

<!-- Updated PHASE-05-05: was Fact(Kind, Value, FilePath, LineStart, LineEnd, Confidence) in spec;
     actual is simplified (Kind, Value, Detail?) — ADR-013 deviation -->

### SymbolRef

```csharp
// Used in SymbolCard.CallsTop to show outgoing calls
public record SymbolRef(
    SymbolId ToSymbol,
    string FullyQualifiedName,
    RefKind Kind,
    FilePath FilePath,
    int Line
);
```

<!-- Updated PHASE-05-05: was SymbolRef(SymbolId, DisplayName, CallCount) in spec;
     actual has different fields — ToSymbol (not SymbolId), no CallCount, adds Kind/FilePath/Line -->

---

## 2.5 ResponseEnvelope\<T>

Every MCP tool response is wrapped in this envelope.

```csharp
public record ResponseEnvelope<T>(
    string Answer,                        // Human-readable summary for the agent
    T Data,                               // Typed payload (varies by tool)
    IReadOnlyList<EvidencePointer> Evidence,     // Reserved — always empty currently
    IReadOnlyList<NextAction> NextActions,        // Reserved — always empty currently
    Confidence Confidence,
    ResponseMeta Meta
);
```

### NextAction

```csharp
public record NextAction(
    string Tool,                           // MCP tool name
    string Rationale,                      // Why this follow-up is suggested
    Dictionary<string, object>? Parameters = null
);
```

<!-- Updated PHASE-05-05: was NextAction(Tool, Dictionary<string,object> Args);
     actual adds Rationale and renames Args → Parameters -->

### ResponseMeta

```csharp
public record ResponseMeta(
    TimingBreakdown Timing,                          // Per-phase timing
    CommitSha BaselineCommitSha,                     // Baseline commit used
    IReadOnlyDictionary<string, LimitApplied> LimitsApplied,  // Budgets that were clamped
    long TokensSaved,                                // Estimated tokens saved this query
    decimal CostAvoided,                             // Cost saved (claude_sonnet rate)
    WorkspaceId? WorkspaceId = null,                 // null in Committed mode
    int OverlayRevision = 0,                         // 0 in Committed mode
    SemanticLevel? SemanticLevel = null,             // null for pre-PHASE-02-08 baselines
    IReadOnlyList<ProjectDiagnostic>? ProjectDiagnostics = null
);
```

<!-- Updated PHASE-05-05: major corrections:
     - CostAvoided is decimal (not Dictionary<string,decimal>) — ADR-013
     - CacheHit field removed (not in actual implementation)
     - TokensSavedTotal/CostAvoidedTotal removed (tracker only, not per-response)
     - Added SemanticLevel and ProjectDiagnostics fields
     - Timing renamed from TimingMs; BaselineCommitSha renamed from CommitSha
     - LimitsApplied type is IReadOnlyDictionary (not nullable Dict) -->

**Token savings accumulator:** Running totals across the daemon session are
tracked in `ITokenSavingsTracker` (injected separately) and persisted to
`~/.codemap/_savings.json`. Not included per-response to avoid bloat.

### TimingBreakdown

```csharp
public record TimingBreakdown(
    double TotalMs,
    double DbQueryMs = 0,
    double CacheLookupMs = 0,
    double RoslynCompileMs = 0,
    double RankingMs = 0
);
```

<!-- Updated PHASE-05-05: field names changed from Total/CacheLookup/DbQuery/etc. to TotalMs/etc.;
     int changed to double -->

### LimitApplied

```csharp
public record LimitApplied(
    int Requested,
    int HardCap    // The clamped value actually applied
);
```

<!-- Updated PHASE-05-05: was LimitApplied(Requested, Applied); field renamed Applied → HardCap -->

### ProjectDiagnostic

```csharp
// Added PHASE-02-08; replaces FailedProjects list in IndexStats
public record ProjectDiagnostic(
    string ProjectName,
    bool Compiled,
    int SymbolCount,
    int ReferenceCount,
    IReadOnlyList<string>? Errors = null
);
```

<!-- Updated PHASE-05-05: added ProjectDiagnostic (was missing from spec) -->

---

## 2.6 CodeMapError

```csharp
public record CodeMapError(
    string Code,
    string Message,
    Dictionary<string, object>? Details = null,
    bool Retryable = false
);
```

---

## 2.7 Result\<T, TError>

All fallible operations return this type.

```csharp
public readonly struct Result<T, TError>
{
    public bool IsSuccess { get; }
    public bool IsFailure { get; }
    public T Value { get; }        // Valid only when IsSuccess
    public TError Error { get; }   // Valid only when IsFailure
    public TResult Match<TResult>(Func<T, TResult> ok, Func<TError, TResult> err);
}
```

**Note:** `using CodeMapResult<T> = Result<T, CodeMapError>` is **[NOT IMPLEMENTED]**.
C# 12 does not support open generic using-aliases. Use `Result<T, CodeMapError>` directly.
See ADR-002.

<!-- Updated PHASE-05-05: removed alias declaration; added NOT IMPLEMENTED note (ADR-002) -->

---

# 3. Global Budgets

| Parameter       | Default | Hard Cap | Applied To                            |
| --------------- | ------- | -------- | ------------------------------------- |
| max_results     | 20      | 100      | symbols.search, refs.find, surfaces   |
| max_references  | 50      | 500      | refs.find, graph.callers/callees      |
| max_depth       | 3       | 6        | graph.callers, graph.callees          |
| max_lines       | 120     | 400      | code.get_span, evidence excerpts      |
| max_chars       | 12,000  | 40,000   | Total response character budget       |

```csharp
public record BudgetLimits(
    int maxResults = 20,
    int maxReferences = 50,
    int maxDepth = 3,
    int maxLines = 120,
    int maxChars = 12_000
);
// BudgetLimits.HardCaps = new(100, 500, 6, 400, 40_000)
// BudgetLimits.Defaults = new()
```

**Rules:**
- Requests omitting budget fields use defaults
- Requests exceeding hard cap are clamped; `limits_applied` in `ResponseMeta` records which fields were clamped
- `BUDGET_EXCEEDED` is NOT returned; clamping is silent (recorded in `LimitsApplied`)

<!-- Updated PHASE-05-05: corrected rule — budgets are clamped silently, not rejected with BUDGET_EXCEEDED -->

---

# 4. Workspace Routing

```csharp
public record RoutingContext(
    RepoId repoId,
    WorkspaceId? workspaceId = null,
    ConsistencyMode consistency = ConsistencyMode.Committed,
    CommitSha? baselineCommitSha = null,    // null = auto-detect from HEAD
    IReadOnlyList<VirtualFile>? virtualFiles = null  // Ephemeral mode only
);
```

<!-- Updated PHASE-05-05: added VirtualFiles property (needed for Ephemeral mode) -->

**Rules:**
- `Committed` → WorkspaceId ignored; baseline only
- `Workspace` → WorkspaceId required; overlay applied
- `Ephemeral` → WorkspaceId required; VirtualFiles allowed for span reads only

---

# 5. Virtual Files (Ephemeral Mode)

```csharp
public record VirtualFile(
    FilePath FilePath,
    string Content
);
```

**Rules:**
- Only valid when `Consistency = Ephemeral`
- VirtualFiles affect span reads (`code.get_span`, `symbols.get_definition_span`) only
- Semantic queries (search, card, refs, graph, hierarchy) use Workspace semantics
- No persistent state change — ephemeral per request

<!-- Updated PHASE-05-05: clarified that VirtualFiles only affect span reads, not semantic queries -->

---

# 6. Tools

All 19 tools are listed below. Tools are organized by milestone.

---

## 6.1 repo.status

**Milestone:** 01

Returns current repository and workspace state snapshot.

**Response Data:**
```csharp
// Private record in RepoStatusHandler — matches this structure:
record RepoStatusResponse(
    RepoId RepoId,
    CommitSha CurrentCommitSha,
    string BranchName,
    bool IsClean,
    bool BaselineIndexExists,
    IReadOnlyList<WorkspaceSummary> Workspaces   // Core.Models.WorkspaceSummary
);

// WorkspaceSummary from CodeMap.Core.Models (used by repo.status):
public record WorkspaceSummary(
    WorkspaceId WorkspaceId,
    RepoId RepoId,
    CommitSha BaselineCommitSha,
    int ChangedFileCount,
    int OverlaySymbolCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt
);
```

<!-- Updated PHASE-05-05: WorkspaceSummary in spec was simplified — actual has RepoId,
     ChangedFileCount, OverlaySymbolCount, CreatedAt, LastAccessedAt.
     Note: workspace.list uses a different richer WorkspaceSummary from WorkspaceManager — see 6.17. -->

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`

---

## 6.2 index.ensure_baseline

**Milestone:** 01

Ensures a baseline index exists for the current HEAD commit. If already indexed, returns immediately. If not, triggers full Roslyn compilation and indexing.

**Response Data:**
```csharp
// Private record in IndexHandler:
record EnsureBaselineResponse(
    CommitSha CommitSha,
    bool AlreadyExisted,    // true = found locally (no Roslyn build)
    IndexStats? Stats,      // null if AlreadyExisted (no new indexing)
    bool FromCache = false  // true = pulled from shared cache (CODEMAP_CACHE_DIR)
);

public record IndexStats(
    int SymbolCount,
    int ReferenceCount,
    int FileCount,
    double ElapsedSeconds,
    Confidence Confidence,
    SemanticLevel SemanticLevel = SemanticLevel.Full,
    IReadOnlyList<ProjectDiagnostic>? ProjectDiagnostics = null
);
```

<!-- Updated PHASE-05-05: IndexStats major correction — actual fields:
     SymbolCount, ReferenceCount, FileCount, ElapsedSeconds, Confidence, SemanticLevel, ProjectDiagnostics.
     Spec had: ProjectCount, FactCount, CompilationErrorCount, FailedProjects (none of these exist).
     Added FromCache field to EnsureBaselineResponse (PHASE-03-08).
     Stats is nullable (null when AlreadyExisted=true). -->

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `COMPILATION_FAILED` (partial — index still created using syntactic fallback)

---

## 6.3 symbols.search

**Milestone:** 01

Search symbols by name, signature, or documentation text via FTS5.

**Parameters (JSON):**
- `repo_path` (required) — absolute path to repo root
- `query` (optional) — FTS5 query string. Omit (or pass `"*"`) to browse all symbols of the specified `kinds` without FTS. Append `*` for prefix match: `"Order*"`. Space = AND, `OR` for alternatives.
- `kinds` (optional, required when `query` is omitted) — array of SymbolKind strings. When provided without `query`, returns all symbols of those kinds via direct SQL (no FTS). Example: `["Class","Interface"]`.
- `workspace_id` (optional)
- `virtual_files` (optional) — for Ephemeral mode
- `namespace` (optional) — prefix match
- `file_path` (optional) — prefix match
- `limit` (optional) — default 20, clamped to max 100

**Query routing (v1.3.1+):**
- `query` provided → FTS5 text search across symbol names, signatures, docs
- `query` omitted (or `"*"`) + `kinds` provided → `GetSymbolsByKindsAsync` (direct SQL, no FTS, returns all symbols of those kinds up to `limit`)
- `query` omitted + no `kinds` → `INVALID_ARGUMENT` error with guidance

**Response Data:**
```csharp
public record SymbolSearchResponse(
    IReadOnlyList<SymbolSearchHit> Hits,
    int TotalCount,
    bool Truncated
);

public record SymbolSearchHit(
    SymbolId SymbolId,
    string FullyQualifiedName,
    SymbolKind Kind,
    string Signature,
    string? DocumentationSnippet,
    FilePath FilePath,
    int Line,
    double Score       // FTS5 BM25 relevance score (negative — lower = better)
);
```

<!-- Updated PHASE-05-05: SymbolSearchHit matches actual; note Score is double not float;
     SymbolSearchFilters.ProjectName exists but is NOT IMPLEMENTED (JOIN deferred) -->

**Note:** `ProjectName` filter in `SymbolSearchFilters` is **[NOT IMPLEMENTED]** — requires JOIN on files table, deferred.

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`

---

## 6.4 symbols.get_card

**Milestone:** 01

Returns a full `SymbolCard` for a specific symbol. Accepts FQN (`symbol_id`)
or stable_id (prefix `sym_`).

**Parameters (JSON):** `repo_path` (required), `symbol_id` (required), `workspace_id` (optional)

**Response Data:** `SymbolCard` (see Section 2.4)

Facts are hydrated from the `facts` table — only `symbols.get_card` hydrates facts, not search.

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.5 code.get_span

**Milestone:** 01

Returns a bounded excerpt from a source file with line numbers. Reads from disk (not from the index). In Ephemeral mode, virtual file content overrides the on-disk file.

**Parameters (JSON):** `repo_path` (required), `file_path` (required), `start_line`, `end_line`, `context_lines`

**Response Data:**
```csharp
public record SpanResponse(
    FilePath FilePath,
    int StartLine,
    int EndLine,
    int TotalFileLines,
    string Content,     // Source text with line numbers: "  42 |     public void Submit()"
    bool Truncated
);
```

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`

---

## 6.6 symbols.get_definition_span

**Milestone:** 01

Convenience wrapper: resolves a symbol's file + span from the index, then
reads the source from disk.

**Parameters (JSON):** `repo_path` (required), `symbol_id` (required), `max_lines` (default 120), `context_lines` (default 2), `workspace_id` (optional)

**Response Data:** `SpanResponse` (same as `code.get_span`)

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.7 refs.find

**Milestone:** 02 — PHASE-02-04

Find all references to a symbol, classified by kind. Returns both resolved and
unresolved edges by default.

**Parameters (JSON):** `repo_path`, `symbol_id` (required); `workspace_id`, `kind`, `resolution_state`, `limit` (optional)

**Response Data:**
```csharp
public record FindRefsResponse(
    SymbolId TargetSymbol,
    IReadOnlyList<ClassifiedReference> References,
    int TotalCount,
    bool Truncated
);

public record ClassifiedReference(
    RefKind Kind,
    SymbolId FromSymbol,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    string? Excerpt,                    // One-line source excerpt (via GetFileSpanAsync)
    ResolutionState ResolutionState = ResolutionState.Resolved,
    string? ToName = null,              // Method name for unresolved edges
    string? ToContainerHint = null      // Receiver expression for unresolved edges
);
```

<!-- Updated PHASE-05-05: ClassifiedReference has additional ResolutionState, ToName, ToContainerHint
     fields not in original spec (added in ADR-022 / PHASE-04-05) -->

**kind filter values:** `Call`, `Read`, `Write`, `Instantiate`, `Override`, `Implementation`

**resolution_state filter values:** `resolved`, `unresolved` (default: both)

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.8 graph.callers

**Milestone:** 02 — PHASE-02-05

Depth-limited upward call graph traversal.

**Parameters (JSON):** `repo_path`, `symbol_id` (required); `workspace_id`, `depth` (default 1, max 6), `limit_per_level` (default 20, max 500) (optional)

**Response Data:**
```csharp
public record CallGraphResponse(
    SymbolId Root,
    IReadOnlyList<CallGraphNode> Nodes,
    int TotalNodesFound,
    bool Truncated
);

public record CallGraphNode(
    SymbolId SymbolId,
    string DisplayName,
    SymbolKind Kind,
    int Depth,
    FilePath? FilePath,    // Nullable: absent for external symbols
    int Line,
    IReadOnlyList<SymbolId> EdgesTo
);
```

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.9 graph.callees

**Milestone:** 02 — PHASE-02-05

Depth-limited downward call graph traversal. Same request/response shape
as `graph.callers` but traverses in the opposite direction.

**Response Data:** `CallGraphResponse` — same structure

---

## 6.10 types.hierarchy

**Milestone:** 02 — PHASE-02-06

Returns base type, implemented interfaces, and derived types for a type symbol.
System.Object is excluded from base type results.

**Parameters (JSON):** `repo_path`, `symbol_id` (required); `workspace_id` (optional)

**Response Data:**
```csharp
public record TypeHierarchyResponse(
    SymbolId TargetType,
    TypeRef? BaseType,          // null for interfaces and System.Object subclasses
    IReadOnlyList<TypeRef> Interfaces,
    IReadOnlyList<TypeRef> DerivedTypes
);

// TypeRef (not SymbolRef — CallCount is meaningless for hierarchy)
public record TypeRef(
    SymbolId SymbolId,
    string DisplayName
);
```

<!-- Updated PHASE-05-05: response uses TypeRef (not SymbolRef) — different from spec;
     System.Object exclusion noted per ADR constraint -->

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.11 workspace.create

**Milestone:** 02 — PHASE-02-02

Creates an overlay workspace for agent-isolated edits. Idempotent — safe to call again if workspace already exists.

**Parameters (JSON):** `repo_path`, `workspace_id`, `solution_path` (required); `commit_sha` (optional)

**Response Data:**
```csharp
public record CreateWorkspaceResponse(
    WorkspaceId WorkspaceId,
    CommitSha BaselineCommitSha,
    int CurrentRevision     // Initially 0
);
```

<!-- Updated PHASE-05-05: field renamed OverlayRevision → CurrentRevision in actual -->

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`

---

## 6.12 workspace.reset

**Milestone:** 02 — PHASE-02-02

Resets an overlay workspace, discarding all overlay data.

**Parameters (JSON):** `repo_path`, `workspace_id` (required)

**Response Data:**
```csharp
public record ResetWorkspaceResponse(
    WorkspaceId WorkspaceId,
    int PreviousRevision,
    int NewRevision    // Always 0 after reset
);
```

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`

---

## 6.13 index.refresh_overlay

**Milestone:** 02 — PHASE-02-01

Incremental overlay refresh for changed files. Auto-detects changed files via `git diff` if `file_paths` not provided. Runs `ResolutionWorker` after reindex.

**Parameters (JSON):** `repo_path`, `workspace_id` (required); `file_paths` (optional array)

**Response Data:**
```csharp
public record RefreshOverlayResponse(
    int FilesReindexed,
    int SymbolsUpdated,
    int NewOverlayRevision
);
```

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `COMPILATION_FAILED`

---

## 6.14 surfaces.list_endpoints

**Milestone:** 03 — PHASE-03-02

Extracts ASP.NET HTTP endpoints (controller routes + minimal API routes).

**Parameters (JSON):** `repo_path` (required); `workspace_id`, `path_filter`, `http_method`, `limit` (optional)

**Response Data:**
```csharp
public record ListEndpointsResponse(
    IReadOnlyList<EndpointInfo> Endpoints,
    int TotalCount,
    bool Truncated
);

public record EndpointInfo(
    string HttpMethod,
    string RoutePath,
    SymbolId HandlerSymbol,
    FilePath FilePath,
    int Line,
    Confidence Confidence
    // IReadOnlyList<string> Attributes — [NOT IMPLEMENTED] deferred
);
```

<!-- Updated PHASE-05-05: EndpointInfo.Attributes field NOT IMPLEMENTED — PHASE-03-02 deviation -->

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`

---

## 6.15 surfaces.list_config_keys

**Milestone:** 03 — PHASE-03-03

Extracts configuration key usage (IConfiguration indexer, GetValue, GetSection, Configure\<T>).

**Parameters (JSON):** `repo_path` (required); `workspace_id`, `key_filter`, `limit` (optional)

**Response Data:**
```csharp
public record ListConfigKeysResponse(
    IReadOnlyList<ConfigKeyInfo> Keys,
    int TotalCount,
    bool Truncated
);

public record ConfigKeyInfo(
    string Key,
    SymbolId UsedBySymbol,
    FilePath FilePath,
    int Line,
    string UsagePattern,   // "IConfiguration indexer", "GetValue", "GetSection", "Options Configure"
    Confidence Confidence
);
```

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`

---

## 6.16 surfaces.list_db_tables

**Milestone:** 03 — PHASE-03-04

Extracts database table references from EF Core DbSet properties, [Table] attributes, and raw SQL strings.

**Parameters (JSON):** `repo_path` (required); `workspace_id`, `table_filter`, `limit` (optional)

**Response Data:**
```csharp
public record ListDbTablesResponse(
    IReadOnlyList<DbTableInfo> Tables,
    int TotalCount,
    bool Truncated
);

public record DbTableInfo(
    string TableName,
    string? Schema,            // e.g., "dbo"; null if no schema prefix
    SymbolId? EntitySymbol,    // DbSet<T> property symbol (not the entity class)
    IReadOnlyList<SymbolId> ReferencedBy,
    Confidence Confidence
);
```

<!-- Updated PHASE-05-05: EntitySymbol is the DbSet<T> property symbol, not the entity class -->

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`

---

## 6.17 workspace.list

**Milestone:** 03 — PHASE-03-07

<!-- Updated PHASE-05-05: tool was missing from spec -->

Lists all active workspaces for the repo with staleness and quality metadata.

**Parameters (JSON):** `repo_path` (required)

**Response Data:**
```csharp
// WorkspaceSummary from WorkspaceManager (richer than repo.status version)
public record WorkspaceSummary(
    WorkspaceId WorkspaceId,
    CommitSha BaseCommitSha,
    int OverlayRevision,
    int ModifiedFileCount,
    bool IsStale,              // BaseCommitSha != current HEAD
    SemanticLevel? SemanticLevel,
    int FactCount,
    DateTimeOffset? CreatedAt  // In-memory only — lost on daemon restart
);

public record WorkspaceListResponse(
    IReadOnlyList<WorkspaceSummary> Workspaces,
    CommitSha CurrentCommitSha   // Current HEAD for client-side staleness comparison
);
```

**Errors:** `INVALID_ARGUMENT`

---

## 6.18 workspace.delete

**Milestone:** 03 — PHASE-03-07

<!-- Updated PHASE-05-05: tool was missing from spec -->

Deletes a workspace and its overlay database.

**Parameters (JSON):** `repo_path`, `workspace_id` (required)

**Response Data:**
```csharp
public record WorkspaceDeleteResponse(
    WorkspaceId WorkspaceId,
    bool Deleted    // false if workspace did not exist
);
```

**Errors:** `INVALID_ARGUMENT`

---

## 6.19 graph.trace_feature

**Milestone:** 04 — PHASE-04-06

<!-- Updated PHASE-05-05: tool was missing from spec -->

Traces a feature end-to-end from an entry point, building a hierarchical call tree annotated with architectural facts at each node.

**Parameters (JSON):** `repo_path`, `entry_point` (required); `workspace_id`, `depth` (default 3, max 6), `limit` (default 100, max 500) (optional)

`entry_point` accepts FQN (`M:Ns.Class.Method(...)`) or stable_id (`sym_` prefix).

**Response Data:**
```csharp
public record FeatureTraceResponse(
    SymbolId EntryPoint,
    string EntryPointName,
    string? EntryPointRoute,    // Route fact if entry point is an endpoint
    IReadOnlyList<TraceNode> Nodes,
    int TotalNodesTraversed,
    int Depth,
    bool Truncated
);

public record TraceNode(
    SymbolId SymbolId,
    StableId? StableId,
    string DisplayName,
    int Depth,
    IReadOnlyList<TraceAnnotation> Annotations,
    IReadOnlyList<TraceNode> Children
);

public record TraceAnnotation(
    string Kind,         // FactKind.ToString() — "Route", "Config", "DbTable", etc.
    string Value,        // Display value (pipe metadata stripped)
    Confidence Confidence
);
```

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

# 7. Error Model

```json
{
  "code": "NOT_FOUND",
  "message": "Symbol 'M:MyNs.MyClass.DoWork' not found in baseline index",
  "details": { "symbol_id": "M:MyNs.MyClass.DoWork" },
  "retryable": false
}
```

### Error Codes

| Code                  | Retryable | Description                                          |
| --------------------- | --------- | ---------------------------------------------------- |
| `INVALID_ARGUMENT`    | No        | Request validation failed (missing required param)   |
| `NOT_FOUND`           | No        | Symbol, file, repo, or workspace not found           |
| `BUDGET_EXCEEDED`     | No        | [NOT IMPLEMENTED] — budgets are clamped silently     |
| `INDEX_NOT_AVAILABLE` | Yes       | Baseline index doesn't exist — call ensure_baseline  |
| `WORKSPACE_REQUIRED`  | No        | Workspace mode but no workspace_id                   |
| `COMPILATION_FAILED`  | Yes       | Roslyn compilation failed (partial results may exist) |

<!-- Updated PHASE-05-05: BUDGET_EXCEEDED is NOT IMPLEMENTED — budgets are silently clamped -->

---

# 8. Observability

Every `ResponseMeta` block includes:

| JSON field            | Type           | Description                                |
| --------------------- | -------------- | ------------------------------------------ |
| `timing`              | TimingBreakdown | Per-phase breakdown: total, cache, db, roslyn, ranking |
| `baseline_commit_sha` | CommitSha      | Baseline commit used for this query        |
| `limits_applied`      | Dict           | Which budget fields were clamped           |
| `tokens_saved`        | long           | Estimated tokens saved vs raw file reads   |
| `cost_avoided`        | decimal        | Cost saved using claude_sonnet rate        |
| `workspace_id`        | WorkspaceId?   | null in Committed mode                     |
| `overlay_revision`    | int            | 0 in Committed mode                        |
| `semantic_level`      | SemanticLevel? | Quality of the baseline index              |

<!-- Updated PHASE-05-05: cost_avoided is single decimal (not Dict by model) — ADR-013;
     removed cache_hit (not in actual); renamed timing_ms → timing -->

Running totals are NOT included per-response. Accumulated in `ITokenSavingsTracker`,
persisted to `~/.codemap/_savings.json`.

---

# 9. Tool-to-Milestone Mapping

| Tool                           | Milestone | Phase       |
| ------------------------------ | --------- | ----------- |
| `repo.status`                  | 01        | PHASE-01-06 |
| `index.ensure_baseline`        | 01        | PHASE-01-06 |
| `symbols.search`               | 01        | PHASE-01-06 |
| `symbols.get_card`             | 01        | PHASE-01-06 |
| `code.get_span`                | 01        | PHASE-01-06 |
| `symbols.get_definition_span`  | 01        | PHASE-01-06 |
| `refs.find`                    | 02        | PHASE-02-04 |
| `graph.callers`                | 02        | PHASE-02-05 |
| `graph.callees`                | 02        | PHASE-02-05 |
| `types.hierarchy`              | 02        | PHASE-02-06 |
| `workspace.create`             | 02        | PHASE-02-02 |
| `workspace.reset`              | 02        | PHASE-02-02 |
| `index.refresh_overlay`        | 02        | PHASE-02-01 |
| `surfaces.list_endpoints`      | 03        | PHASE-03-02 |
| `surfaces.list_config_keys`    | 03        | PHASE-03-03 |
| `surfaces.list_db_tables`      | 03        | PHASE-03-04 |
| `workspace.list`               | 03        | PHASE-03-07 |
| `workspace.delete`             | 03        | PHASE-03-07 |
| `graph.trace_feature`          | 04        | PHASE-04-06 |

<!-- Updated PHASE-05-05: added workspace.list, workspace.delete, graph.trace_feature (tools 17-19) -->

---

# End of Document
