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

## 1.2 Backend Engine (CodeMap.Query → CodeMap.Storage.Engine → CodeMap.Roslyn)

Core semantic engine. MCP Façade must NOT contain:
- Indexing logic
- Roslyn references
- Direct storage-engine access
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
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? TargetFrameworks = null,        // M20-01: multi-target only
    IReadOnlyList<string>? GeneratorLoadFailures = null,   // v2.8.2 (ADR-046)
    string? MissingRestoreOutput = null                    // v2.8.2 (ADR-054)
);
```

`missing_restore_output` (omitted when null) is the solution-relative path where the project's
`project.assets.json` was expected, e.g. `"src/Web/obj/project.assets.json"`: the project uses NuGet
restore but the checkout was never restored (a fresh clone or git worktree; `obj/` is gitignored and
CodeMap doesn't restore). If the project also has compile errors, its package references (sometimes the
target framework itself) are **unresolved**: `semantic_level` is then at most `partial`, the baseline
isn't pushed to a shared cache, and a warning names the fix (`dotnet restore`, then rebuild the
baseline). Without errors (e.g. no packages, framework shipped with the SDK) it's informational only.
See KNOWN-LIMITATIONS L-12.

The run's `semantic_level` (in `index_ensure_baseline` and in every query's `meta`) is computed by one
rule for the build and the stored baseline (`SemanticLevels`, ADR-054): `full` when every project
compiled without an environment degradation (`generator_load_failures`, or `missing_restore_output`
with errors), `syntax_only` when none compiled, otherwise `partial`.

`generator_load_failures` (omitted when null) lists each source-generator assembly the bundled
Roslyn refused to load, e.g. `"Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler
(built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)"`. Code those generators produce (Razor components,
`[LoggerMessage]` implementations, …) is **missing from the index**; the run's `semantic_level` is then
at most `partial` and `confidence` at most `medium`. Fix: update CodeMap, or index with an SDK
whose generators match (see KNOWN-LIMITATIONS L-11).

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

**Codes** (`CodeMap.Core.Errors.ErrorCodes`):

| Code | Meaning | Retryable |
|---|---|---|
| `INVALID_ARGUMENT` | Caller input failed validation (missing/bad argument, path, commit, id) | no |
| `NOT_FOUND` | Symbol or resource not in the index | no |
| `AMBIGUOUS` | Name matched several symbols; `details.candidates` lists them | no |
| `BUDGET_EXCEEDED` | A result/line/char hard cap was exceeded | no |
| `INDEX_NOT_AVAILABLE` | No baseline for repo + commit — run `index_ensure_baseline` | yes |
| `WORKSPACE_REQUIRED` | Workspace-scoped call without a workspace id | no |
| `COMPILATION_FAILED` | Roslyn compilation produced no symbols (real build failure only) | yes, after fixing the build |
| `WORKSPACE_IN_USE` *(v2.8.2)* | Another CodeMap process holds this workspace's overlay (WAL locked); `details.workspace_id` | yes — retry later or use another `workspace_id` |
| `STORAGE_ERROR` *(v2.8.2)* | I/O or storage-engine failure: busy/incomplete baseline, disk, access denied | yes |
| `INTERNAL_ERROR` *(v2.8.2)* | Unexpected exception inside a tool handler (a bug); `details.exception_type` when it escaped the handler | no |

Before v2.8.2, storage/I-O failures in `index_ensure_baseline` were reported as
`COMPILATION_FAILED`, and in `workspace.*` / `index_refresh_overlay` / `repo_status` as
`INVALID_ARGUMENT` (ADR-041).

**Every tool failure uses this envelope** (`result.isError: true`), on every tool, including
exceptions no handler caught (ADR-045). Those are classified at the `tools/call` boundary: sharing
violation → `WORKSPACE_IN_USE` (with an explicit `workspace_id`) or `STORAGE_ERROR`; storage / I-O →
`STORAGE_ERROR`; anything else, **including an `ArgumentException`**, → `INTERNAL_ERROR` (input
validation is the handler's job, so an argument exception reaching the boundary is a bug). JSON-RPC
`-32603` is reserved for protocol-level failures outside any tool (`initialize`, `tools/list`, framing).
Before v2.8.2, an exception escaping a read-only tool arrived as `-32603`.

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
| max_results     | 20      | 100      | symbols_search, refs_find, surfaces   |
| max_references  | 50      | 500      | refs_find, graph_callers/callees      |
| max_depth       | 3       | 6        | graph_callers, graph_callees          |
| max_lines       | 120     | 400      | code_get_span, evidence excerpts      |
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
- VirtualFiles affect span reads (`code_get_span`, `symbols_get_definition_span`) only
- Semantic queries (search, card, refs, graph, hierarchy) use Workspace semantics
- No persistent state change — ephemeral per request

<!-- Updated PHASE-05-05: clarified that VirtualFiles only affect span reads, not semantic queries -->

---

# 6. Tools

The server registers 28 tools. The sections below document the core set, organized by milestone;
`codemap_guide` returns the full list with one-line descriptions (`verbose: true`).

### Destructive tools: `index_remove_repo`, `index_cleanup` (v2.10.0, PHASE-21-10)

- **`repo_path` is required** on both. There is no auto-default: a missing `repo_path` returns
  `INVALID_ARGUMENT` naming the repos this process knows. `dry_run` defaults to `true`.
- **`index_cleanup`** never removes the current HEAD or the baseline of any workspace of any process.
  - `workspace_create` writes `overlays/<ws>/overlay.meta.json` (`baseline_commit_sha`), so other
    processes can see which baseline a workspace needs. Those baselines are listed in
    `protected_by_workspaces`.
  - A workspace directory without that file (created before v2.10.0) is listed in
    `workspaces_without_baseline_record`.
  - If such a workspace is live in another process, a non-dry-run cleanup returns `WORKSPACE_IN_USE`
    and deletes nothing.
  - A baseline-level overlay (`overlays/<commit sha>/`) is not a workspace: it is removed with its baseline.
  - Response additions: `protected_by_workspaces`, `skipped_in_use`, `workspaces_without_baseline_record`,
    `workspaces_in_use`.
- **`index_remove_repo`** removes the repo's baselines **and workspace overlays**.
  - If another CodeMap process has a workspace open in the repo (its `overlay.lock` is held), the call
    returns `WORKSPACE_IN_USE` with `details.workspace_ids` and deletes nothing. A dry run lists those
    workspaces in `workspaces_in_use`.
  - This process's own workspaces are closed and deleted, and the sticky default is cleared.
- **No partial deletes:**
  - A baseline is removed only when none of its files is open elsewhere. It is renamed aside in one
    step, then deleted.
  - A baseline in use stays complete and is listed in `skipped_in_use`. It isn't counted in
    `baselines_removed` / `bytes_freed` / `removed_commits`.
- **`index_remove_repo` response additions** (optional, omitted when empty): `workspaces_removed`,
  `skipped_in_use`, `workspaces_in_use`.

### Parameter types sent as strings (v2.10.0 for booleans)

Some MCP clients send every parameter as a JSON string. Integer parameters (`limit`, `depth`, …) accept
`8000` and `"8000"`; since v2.10.0 boolean parameters (`include_code`, `include_facts`, `verbose`,
`dry_run`, `follow_interface`) accept `true` and `"true"` (any case). Any other value gives the parameter's
default; for `dry_run` that is `true`, so a malformed value never deletes anything.

### Tool names and aliases (v2.9.0, ADR-051 / ADR-056)

- **Canonical names** use underscores (`symbols_search`) and match `^[a-zA-Z0-9_-]{1,64}$`, the rule
  LLM APIs apply to tool names. `tools/list` returns only these. The constants are
  `CodeMap.Core.Models.ToolNames`.
- **Deprecated aliases:** the pre-v2.9.0 names put a dot instead of the first underscore
  (`symbols` + `.` + `search`). A `tools/call` with such a name runs the canonical tool:
  - `content[0]` is identical to the canonical call's envelope;
  - `content[1]` is a text note naming the canonical tool and the removal version;
  - `isError` is unchanged;
  - the server logs one `DeprecatedToolAlias` warning per alias per process.
- **Removal:** v2.11.0 at the earliest (`ToolNames.AliasRemovalVersion`). `codemap_guide` reports it
  in `deprecated_aliases: { note, removal_version }`.
- **Who can reach an alias:** clients that validate names against `tools/list` before calling (for
  example MCP Inspector) never send it. Aliases serve clients with a cached pre-v2.9.0 tool list,
  scripts and direct JSON-RPC callers.
- **Unknown names** return JSON-RPC `-32602` with a "Did you mean: <canonical>?" suggestion, taken from
  the closest canonical name or alias.

---

## 6.1 repo_status

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

// WorkspaceSummary from CodeMap.Core.Models (used by repo_status):
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
     Note: workspace_list uses a different richer WorkspaceSummary from WorkspaceManager — see 6.17. -->

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `STORAGE_ERROR` (retryable), `INTERNAL_ERROR`

---

## 6.2 index_ensure_baseline

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

**Errors:** `INVALID_ARGUMENT` (bad/missing solution_path, unresolvable commit), `COMPILATION_FAILED` (compilation produced no symbols — only for real compile failures since v2.8.2), `STORAGE_ERROR` (retryable; I/O or baseline busy), `INTERNAL_ERROR`

---

## 6.3 symbols_search

**Milestone:** 01

Search symbols by name, namespace, signature, or documentation words (v2 search index; not SQLite FTS5 since v2.1.0).

**Parameters (JSON):**
- `repo_path` (required) — absolute path to repo root
- `query` (optional) — search query. Omit (or pass `"*"`) to browse all symbols of the specified `kinds`.
  Syntax (PHASE-21-08, ADR-055):
  - words are matched **by prefix** against symbol tokens (`Order` finds `OrderService`); a trailing `*` is accepted and ignored;
  - space = AND (every word must match); an explicit `AND` is accepted;
  - `OR` (uppercase) separates alternatives, lowest precedence: `A B OR C` = `(A AND B) OR C`; each alternative is ranked on its own;
  - `"quoted words"` = all of those words (no adjacency: the index stores token sets, not positions);
  - parentheses are ignored (no nesting); `NOT` / `NEAR` → `INVALID_ARGUMENT`; a query with only operators → `INVALID_ARGUMENT`.
- `kinds` (optional, required when `query` is omitted) — array of SymbolKind strings. When provided without `query`, returns all symbols of those kinds via direct SQL (no FTS). Example: `["Class","Interface"]`.
- `workspace_id` (optional)
- `virtual_files` (optional) — for Ephemeral mode
- `namespace` (optional) — prefix match
- `file_path` (optional) — prefix match
- `limit` (optional) — default 20, clamped to max 100

**Query routing (v1.3.1+):**
- `query` provided → search-index lookup across symbol names, namespaces, signatures, doc words
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
    double Score       // v2 relevance score (higher = better: exact name, FQN suffix, prefix, token coverage)
);
```

<!-- Updated PHASE-05-05: SymbolSearchHit matches actual; note Score is double not float;
     SymbolSearchFilters.ProjectName exists but is NOT IMPLEMENTED (JOIN deferred) -->

**Note:** `ProjectName` filter in `SymbolSearchFilters` is **[NOT IMPLEMENTED]** — requires JOIN on files table, deferred.

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`

---

## 6.4 symbols_get_card

**Milestone:** 01

Returns a full `SymbolCard` for a specific symbol. Accepts FQN (`symbol_id`)
or stable_id (prefix `sym_`).

**Parameters (JSON):** `repo_path` (required), `symbol_id` (required), `workspace_id` (optional)

**Response Data:** `SymbolCard` (see Section 2.4)

Facts are hydrated from the `facts` table — only `symbols_get_card` hydrates facts, not search.

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.5 code_get_span

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

## 6.6 symbols_get_definition_span

**Milestone:** 01

Convenience wrapper: resolves a symbol's file + span from the index, then
reads the source from disk.

**Parameters (JSON):** `repo_path` (required), `symbol_id` (required), `max_lines` (default 120), `context_lines` (default 2), `workspace_id` (optional)

**Response Data:** `SpanResponse` (same as `code_get_span`)

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `INDEX_NOT_AVAILABLE`

---

## 6.7 refs_find

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

## 6.8 graph_callers

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

## 6.9 graph_callees

**Milestone:** 02 — PHASE-02-05

Depth-limited downward call graph traversal. Same request/response shape
as `graph_callers` but traverses in the opposite direction.

**Response Data:** `CallGraphResponse` — same structure

---

## 6.10 types_hierarchy

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

## 6.11 workspace_create

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

**Errors:** `INVALID_ARGUMENT`, `INDEX_NOT_AVAILABLE`, `WORKSPACE_IN_USE` (retryable; another CodeMap process holds this workspace), `STORAGE_ERROR` (retryable), `INTERNAL_ERROR`

---

## 6.12 workspace_reset

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

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `WORKSPACE_IN_USE` (retryable), `STORAGE_ERROR` (retryable), `INTERNAL_ERROR`

---

## 6.13 index_refresh_overlay

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

**Errors:** `INVALID_ARGUMENT`, `NOT_FOUND`, `COMPILATION_FAILED`, `WORKSPACE_IN_USE` (retryable), `STORAGE_ERROR` (retryable), `INTERNAL_ERROR`

---

## 6.14 surfaces_list_endpoints

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

## 6.15 surfaces_list_config_keys

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

## 6.16 surfaces_list_db_tables

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

## 6.17 workspace_list

**Milestone:** 03 — PHASE-03-07

<!-- Updated PHASE-05-05: tool was missing from spec -->

Lists all active workspaces for the repo with staleness and quality metadata.

**Parameters (JSON):** `repo_path` (required)

**Response Data:**
```csharp
// WorkspaceSummary from WorkspaceManager (richer than repo_status version)
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

**Errors:** `INVALID_ARGUMENT`, `STORAGE_ERROR` (retryable), `INTERNAL_ERROR`

---

## 6.18 workspace_delete

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

**Errors:** `INVALID_ARGUMENT`, `WORKSPACE_IN_USE` (retryable), `STORAGE_ERROR` (retryable), `INTERNAL_ERROR`

---

## 6.19 graph_trace_feature

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
| `repo_status`                  | 01        | PHASE-01-06 |
| `index_ensure_baseline`        | 01        | PHASE-01-06 |
| `symbols_search`               | 01        | PHASE-01-06 |
| `symbols_get_card`             | 01        | PHASE-01-06 |
| `code_get_span`                | 01        | PHASE-01-06 |
| `symbols_get_definition_span`  | 01        | PHASE-01-06 |
| `refs_find`                    | 02        | PHASE-02-04 |
| `graph_callers`                | 02        | PHASE-02-05 |
| `graph_callees`                | 02        | PHASE-02-05 |
| `types_hierarchy`              | 02        | PHASE-02-06 |
| `workspace_create`             | 02        | PHASE-02-02 |
| `workspace_reset`              | 02        | PHASE-02-02 |
| `index_refresh_overlay`        | 02        | PHASE-02-01 |
| `surfaces_list_endpoints`      | 03        | PHASE-03-02 |
| `surfaces_list_config_keys`    | 03        | PHASE-03-03 |
| `surfaces_list_db_tables`      | 03        | PHASE-03-04 |
| `workspace_list`               | 03        | PHASE-03-07 |
| `workspace_delete`             | 03        | PHASE-03-07 |
| `graph_trace_feature`          | 04        | PHASE-04-06 |

<!-- Updated PHASE-05-05: added workspace_list, workspace_delete, graph_trace_feature (tools 17-19) -->

---

# End of Document
