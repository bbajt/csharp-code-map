# ARCHITECTURE-WALKTHROUGH.MD

## CodeMap — Architecture Walkthrough

This document traces requests through CodeMap end-to-end, layer by layer,
with real class names, method signatures, and SQL queries. It's the answer
to "how does this actually work?" rather than "what are the contracts?"

For API contracts, see [API-SCHEMA.MD](API-SCHEMA.MD).
For practical how-to recipes, see [DEVELOPER-GUIDE.MD](DEVELOPER-GUIDE.MD).
For design decisions, see [DECISIONS.MD](DECISIONS.MD).

---

## The Big Picture

```
stdin (JSON-RPC)
      │
      ▼
┌─────────────────────────────────────────┐
│  McpServer (CodeMap.Mcp/McpServer.cs)   │  Hand-rolled stdio JSON-RPC (ADR-015)
│  Reads requests, dispatches via         │
│  ToolRegistry, writes responses         │
└─────────────────────────────────────────┘
      │
      ▼
┌─────────────────────────────────────────┐
│  Handler (e.g. McpToolHandlers,         │  Parameter parsing, routing,
│  GraphHandler, RefsHandler, etc.)       │  INVALID_ARGUMENT guards
└─────────────────────────────────────────┘
      │
      ▼
┌─────────────────────────────────────────┐
│  MergedQueryEngine (IQueryEngine)       │  Overlay + baseline merge per tool
│  ├── inner: QueryEngine                 │  Decorator pattern
│  ├── OverlayStore (IOverlayStore)       │
│  └── WorkspaceManager                  │
└─────────────────────────────────────────┘
      │
      ├── Workspace hit → OverlayStore → overlay SQLite DB
      │
      └── Baseline path:
┌─────────────────────────────────────────┐
│  QueryEngine                            │  L1 cache check → SQLite query
│  ├── InMemoryCacheService (L1)          │  TimingContext per phase
│  └── BaselineStore (ISymbolStore)       │
└─────────────────────────────────────────┘
      │
      ▼
┌─────────────────────────────────────────┐
│  SQLite (WAL mode)                      │  ~/.codemap/baselines/{repoId}/{sha}.db
│  symbols, symbols_fts, refs,            │  Separate DB per (repo, commit)
│  type_relations, facts, files           │
└─────────────────────────────────────────┘
stdout (JSON-RPC response)
```

**Layering philosophy:** `CodeMap.Core` defines all interfaces and types.
Every other layer depends only on Core interfaces, never on sibling
implementations. `CodeMap.Daemon` is the sole point where all layers are
wired together via DI. This means you can test `QueryEngine` with a mocked
`ISymbolStore`, test a handler with a mocked `IQueryEngine`, and the only
project that knows the full graph is the composition root.

---

## Walkthrough: symbols.search (Happy Path)

### 1. Request arrives on stdin

`McpServer.RunAsync` reads a JSON-RPC message line from stdin:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "symbols.search",
    "arguments": {
      "repo_path": "/my/repo",
      "query": "OrderService",
      "limit": 10
    }
  }
}
```

`McpServer` deserializes this with `System.Text.Json` and looks up
`"symbols.search"` in the `ToolRegistry`.

### 2. ToolRegistry dispatches to the handler

`ToolRegistry.InvokeAsync` finds the registered `Func<JsonObject?, CancellationToken, Task<ToolCallResult>>`
for `"symbols.search"` — which is `McpToolHandlers.HandleSearchAsync`.

### 3. Handler parses parameters and builds a RoutingContext

`McpToolHandlers.HandleSearchAsync` reads `repo_path`, `query`, and
optional `workspace_id`, `kinds`, `namespace`, `file_path`, `limit`.

It calls `BuildRoutingResultAsync` to construct the `RoutingContext`:

```csharp
var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct);
var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct);
// workspace_id absent → Committed mode
var routing = new RoutingContext(repoId: repoId, baselineCommitSha: sha);
```

`GetRepoIdentityAsync` computes a stable `RepoId` by hashing the remote
origin URL (SHA-256, first 16 hex chars). No remote → hashes the local
absolute path prefixed with `"local-"`.

### 4. MergedQueryEngine receives the call

`MergedQueryEngine.SearchSymbolsAsync` is the implementation behind
`IQueryEngine`. In Committed mode (no workspace_id), it delegates directly
to the inner `QueryEngine.SearchSymbolsAsync`.

### 5. QueryEngine checks the L1 cache

```csharp
using var timing = new TimingContext();
var cacheKey = $"search:{repoId}:{commitSha}:{query}:{limit}";
timing.EndCacheLookup();

if (_cache.TryGet(cacheKey, out IReadOnlyList<SymbolRef>? cached))
    return BuildOkEnvelope(cached!, timing, tokensSaved: EstimateTokensSaved(cached!.Count));
```

Cache hit returns in ~17 µs (measured in BenchmarkDotNet). Cache is an
`InMemoryCacheService` capped at 10,000 entries with approximate LRU
eviction (expired-first, then oldest 10% by last-access time).

### 6. Cache miss → FTS5 query

```csharp
var results = await _store.SearchSymbolsAsync(
    repoId, commitSha, query, kinds, limit + 1, ct);  // +1 to detect truncation
timing.EndDbQuery();
```

`BaselineStore.SearchSymbolsAsync` runs:

```sql
SELECT s.symbol_id, s.display_name, s.symbol_kind, s.namespace,
       s.file_path, s.loc_start, s.stable_id
FROM symbols_fts
JOIN symbols s USING(symbol_id)
WHERE symbols_fts MATCH $query
ORDER BY rank
LIMIT $limit
```

The `MATCH` expression is the user query with a `*` suffix appended
(e.g., `"OrderService*"`). FTS5 BM25 ranking orders results by relevance.

Note: the query is `limit + 1` rows. If exactly `limit + 1` rows come back,
the traverser knows the result is truncated and sets `Truncated: true` in
the envelope.

### 7. Results ranked and enveloped

`QueryEngine` ranks/trims results, stores in cache, then builds the response:

```csharp
var answer = AnswerGenerator.ForSearch(results.Count, query, truncated);
// e.g., "Found 3 symbols matching 'OrderService'"

var envelope = new ResponseEnvelope<IReadOnlyList<SymbolRef>>(
    Data: results,
    Answer: answer,
    Meta: new ResponseMeta(
        RepoId: repoId,
        CommitSha: commitSha,
        TimingMs: timing.ToBreakdown(),
        CostAvoided: tokensSaved * SonnetRatePerToken,
        SemanticLevel: indexedLevel));
```

`TimingMs` has three real measurements: `CacheLookupMs`, `DbQueryMs`,
`RankingMs` — populated by `TimingContext.StartPhase/EndCacheLookup/EndDbQuery`.

### 8. Response serialized to stdout

`McpServer` wraps the `ToolCallResult` in a JSON-RPC response and writes
it to stdout. The content is already a JSON string (serialized by the handler
using `CodeMapJsonOptions.Default` with snake_case_lower naming).

Logs emitted during the request go to stderr and the daily log file. Stdout
receives only the JSON-RPC response — never a log line.

---

## Walkthrough: symbols.get_card (With Fact Hydration)

The card lookup adds one extra step compared to search: fact hydration.

### 1–3. Same as search

Handler parses `symbol_id`, builds `RoutingContext`, calls
`MergedQueryEngine.GetSymbolCardAsync`.

### 4. Symbol lookup

```csharp
// QueryEngine.GetSymbolCardAsync
var card = await _store.GetSymbolAsync(repoId, commitSha, symbolId, ct);
```

```sql
SELECT symbol_id, display_name, symbol_kind, namespace, signature,
       file_path, loc_start, loc_end, stable_id, project_name
FROM symbols
WHERE symbol_id = $symbolId
```

### 5. Fact hydration (separate query)

```csharp
var facts = await _store.GetFactsForSymbolAsync(repoId, commitSha, symbolId, ct);
card = card with { Facts = facts.Select(f => new Fact(f.Kind, f.Value)).ToList() };
```

```sql
SELECT f.fact_kind, f.value, f.loc_start, f.loc_end, f.confidence
FROM facts f
JOIN files fi ON f.file_id = fi.file_id
WHERE f.symbol_id = $symbolId
ORDER BY f.fact_kind, f.loc_start
```

Note that `Fact` in `SymbolCard` only exposes `Kind` and `Value` — not the
full location/confidence from `StoredFact`. Full surface details are available
via the `surfaces.*` tools.

### 6. TimingBreakdown shows both phases

`TimingMs` will show `CacheLookupMs: 0`, `DbQueryMs: 4` (two queries — symbol
+ facts), `RankingMs: 0` for a card lookup.

---

## Walkthrough: Workspace Mode (Overlay Merge)

When a request includes `workspace_id`, `MergedQueryEngine` intercepts the
call and merges overlay data with the baseline. The merge strategy differs
per method — here we trace `symbols.search`.

### Setup: how workspaces are created

```json
{"name":"workspace.create","arguments":{"repo_path":"/my/repo","workspace_id":"ws-agent-1","solution_path":"/my/repo/MyApp.sln"}}
```

`WorkspaceHandler.HandleCreateAsync` calls `WorkspaceManager.CreateWorkspaceAsync`,
which creates an overlay SQLite database at
`~/.codemap/overlays/{repoId}/{workspaceId}.db`. The overlay schema mirrors
the baseline schema and adds `deleted_symbols` and `overlay_meta` tables.

### Incremental refresh

When the agent edits files and calls `index.refresh_overlay`, `WorkspaceManager`
orchestrates:

1. `IGitService.GetChangedFilesAsync` → diff vs baseline commit
2. `IIncrementalCompiler.RecompileFilesAsync` → MSBuildWorkspace (cached from
   the initial index) re-compiles only the changed documents via
   `Solution.WithDocumentText`
3. `OverlayStore.ApplyDeltaAsync` → writes new symbols, refs, facts,
   type_relations for changed files; marks deleted symbols in `deleted_symbols`
4. `IResolutionWorker.ResolveOverlayEdgesAsync` → FTS-based resolution of
   syntactic refs produced during fallback compilation

### Overlay merge for symbols.search

`MergedQueryEngine.SearchSymbolsAsync` (Workspace mode):

```csharp
// 1. Get overlay symbols (files the agent has modified)
var overlaySymbols = await _overlayStore.SearchOverlaySymbolsAsync(wsId, query, limit, ct);

// 2. Get baseline symbols (all other files)
var baselineSymbols = await _inner.SearchSymbolsAsync(committedRouting, query, limit, ct);

// 3. Get deleted symbol IDs (symbols removed from modified files)
var deletedIds = await _overlayStore.GetDeletedSymbolIdsAsync(wsId, ct);

// 4. Merge: overlay UNION baseline, minus deleted, dedup by FQN (overlay wins)
var merged = Merge(overlaySymbols, baselineSymbols.Data, deletedIds);
```

The key insight: baseline symbols from files that the overlay has reindexed
are excluded. This is "file-authoritative" merging — the overlay is the
authority for its files, and the baseline is the authority for everything
else.

### Overlay merge for symbols.get_card

`MergedQueryEngine.GetSymbolCardAsync` (Workspace mode):

```csharp
// 1. Try overlay first
var overlayCard = await _overlayStore.GetOverlaySymbolAsync(wsId, symbolId, ct);
if (overlayCard is not null)
{
    if (deletedIds.Contains(symbolId)) return NotFound();
    // Hydrate with overlay facts (overlay-wins over baseline facts)
    var overlayFacts = await _overlayStore.GetOverlayFactsForSymbolAsync(wsId, symbolId, ct);
    return overlayCard with { Facts = overlayFacts.Select(f => new Fact(f.Kind, f.Value)).ToList() };
}

// 2. Fall back to baseline (symbol not in any modified file)
return await _inner.GetSymbolCardAsync(committedRouting, symbolId, ct);
```

Stable_id fallback: if a FQN miss occurs (symbol renamed), `MergedQueryEngine`
tries looking up by `stable_id` in both overlay and baseline stores.

---

## Walkthrough: Degraded Path (Compilation Failure)

CodeMap degrades gracefully when a project fails to compile. Here's what
happens.

### 1. Compilation attempt

`RoslynCompiler.CompileAndExtractAsync` opens the solution with
`MSBuildWorkspace.Create()` and calls `GetCompilationAsync` per project.
If a project throws (e.g., missing SDK, unresolvable references):

```csharp
try
{
    var compilation = await project.GetCompilationAsync(ct);
    // Semantic extraction...
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Compilation failed for {Project}", project.Name);
    // Fallback to syntactic extraction
    var syntacticSymbols = SyntacticFallback.ExtractAll(project.Documents, ...);
    var syntacticRefs = SyntacticReferenceExtractor.ExtractAll(project.Documents, ...);
    allSymbols.AddRange(syntacticSymbols);
    allRefs.AddRange(syntacticRefs);
}
```

### 2. Syntactic extraction produces unresolved edges

`SyntacticReferenceExtractor` walks the AST without a `SemanticModel`.
It can identify that `_orderRepo.Save(order)` is called, but not the fully
qualified symbol ID for `_orderRepo.Save`. Unresolved edges are stored with:

- `to_symbol_id = ""` (empty)
- `to_name = "Save"` (method name from AST)
- `to_container_hint = "_orderRepo"` (receiver expression from AST)
- `resolution_state = "unresolved"`
- `from_symbol_id` in simplified `"ClassName::MethodName"` format (not FQN)

### 3. SemanticLevel reflects the degradation

The compiler sets `SemanticLevel` in the returned `CompilationResult`:

- `Full` — all projects compiled successfully
- `Partial` — at least one compiled, at least one failed
- `SyntaxOnly` — all projects failed

This level is stored in `repo_meta` and returned in every `ResponseMeta.SemanticLevel`.
Agents can inspect this to decide whether to re-index or trust the results.

### 4. refs.find returns unresolved edges by default

By default, `refs.find` returns both `resolved` and `unresolved` edges.
Agents working with a partially-indexed codebase see unresolved edges with
`to_name` and `to_container_hint` populated, which often provides enough
context to navigate manually.

```json
{
  "symbol_id": "",
  "to_name": "Save",
  "to_container_hint": "_orderRepo",
  "resolution_state": "unresolved",
  "kind": "Call"
}
```

To filter: `{"resolution_state": "resolved"}` or `{"resolution_state": "unresolved"}`.

### 5. Self-healing: developer fixes the code

1. Developer pushes a fix and the agent calls `index.refresh_overlay`.
2. `IncrementalCompiler` re-compiles the previously-broken project — now succeeds.
3. `OverlayStore.ApplyDeltaAsync` writes new semantic refs (resolved) for
   the fixed files.
4. `ResolutionWorker.ResolveOverlayEdgesAsync` runs automatically (triggered
   by `WorkspaceManager` after delta apply, skipped when `SemanticLevel == SyntaxOnly`).
5. Resolution: FTS search finds `to_name` + `to_container_hint` matches in
   the symbol index → upgrades edge to resolved via `UpgradeOverlayEdgeAsync`.
6. `UpgradeEdgeAsync` sets `to_symbol_id`, clears `to_name` and `to_container_hint`
   to NULL.

After self-healing, `refs.find` returns full `to_symbol_id` — the agent can
follow the reference graph again.

---

## Walkthrough: index.ensure_baseline (Full Pipeline)

This is the most expensive path in CodeMap. Here's every step in order.

### 1. Local check (fast)

`IndexHandler.HandleEnsureBaselineAsync` calls:

```csharp
var exists = await _store.BaselineExistsAsync(repoId, sha, ct);
```

`BaselineExistsAsync` checks whether the SQLite database file exists at
`~/.codemap/baselines/{repoId}/{sha}.db` — it does NOT check symbol count
(ADR-018). A zero-symbol baseline for an empty project is still valid.

If the baseline exists, return immediately with `FromCache: false`.

### 2. Cache check (fast — ~1.6ms)

```csharp
var pulled = await _cacheManager.PullAsync(repoId, sha, ct);
```

`BaselineCacheManager.PullAsync` looks for the DB file in `CODEMAP_CACHE_DIR`
and copies it locally if found. The copy is ~1.6ms for a typical DB.

The cache manager validates the pulled file using the SQLite magic header
(`"SQLite format 3\0"` at offset 0, 16 bytes) before trusting it. A corrupt
cache entry is ignored (self-healing — next build pushes a fresh copy).

If pulled from cache, return immediately with `FromCache: true`.

### 3. Full Roslyn compilation (~1–20s depending on solution size)

```csharp
var result = await _compiler.CompileAndExtractAsync(solutionPath, repoRootPath, ct);
```

`RoslynCompiler.CompileAndExtractAsync`:

a. `MSBuildWorkspace.Create()` — loads the solution file.
b. For each project, `GetCompilationAsync()` — triggers the Roslyn compiler.
c. **Symbol extraction:** `SymbolExtractor.ExtractAll(compilation)` — walks
   all `INamedTypeSymbol`, `IMethodSymbol`, `IPropertySymbol`, etc. to produce
   `SymbolCard` records with FQN `symbol_id`, display name, signature, file location.
d. **Reference extraction:** `ReferenceExtractor.ExtractAll(compilation)` —
   walks all method bodies for call/read/write/override/instantiate references.
e. **Type relation extraction:** `TypeRelationExtractor.ExtractAll(compilation)` —
   records base class and interface inheritance edges (System.Object excluded).
f. **Fact extraction (6 kinds):**
   - `EndpointExtractor` — controller routes + minimal API routes
   - `ConfigKeyExtractor` — IConfiguration indexer, GetValue, GetSection, Configure<T>
   - `DbTableExtractor` — DbSet<T>, [Table] attribute, raw SQL (regex, Medium confidence)
   - `DiRegistrationExtractor` — AddScoped/Singleton/Transient, TryAdd, AddHostedService
   - `MiddlewareExtractor` — Use*/Map* on IApplicationBuilder
   - `RetryPolicyExtractor` — RetryAsync, WaitAndRetry, AddResilienceHandler (name-based)
g. **Stable ID computation:** `SymbolFingerprinter.ComputeStableIds()` —
   SHA-256 of `Kind|ContainerFQN|Namespace|IsStatic|ReturnType|Arity|ParamTypes`.
   Ordinal disambiguation for overloads. Returns a `Dictionary<SymbolId, StableId>`.
h. All extraction data is patched with stable IDs.

### 4. Baseline persistence (single SQLite transaction)

```csharp
await _store.CreateBaselineAsync(repoId, commitSha, result, repoRootPath, ct);
```

`BaselineStore.CreateBaselineAsync` runs in a single write transaction:

```sql
INSERT INTO files (file_id, file_path, project_name, content_hash) VALUES ...
INSERT INTO symbols (symbol_id, stable_id, display_name, ...) VALUES ...
INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')  -- FTS sync
INSERT INTO refs (ref_id, from_symbol_id, to_symbol_id, ref_kind, ...) VALUES ...
INSERT INTO type_relations (relation_id, type_symbol_id, related_symbol_id, ...) VALUES ...
INSERT INTO facts (fact_id, symbol_id, stable_id, fact_kind, value, ...) VALUES ...
PRAGMA wal_checkpoint(TRUNCATE)
```

FK violations are skipped: symbols and refs for files not in the compilation
(e.g., framework assemblies from `System.*`) are excluded to avoid FK errors
on the `files` table.

FTS is synced via `INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')`
rather than a trigger, because SQLite FTS5 does not support `AFTER INSERT`
triggers with semicolons (ADR-017 constraint).

### 5. Cache push (async, fire-and-forget for errors)

```csharp
await _cacheManager.PushAsync(repoId, sha, ct);
```

`BaselineCacheManager.PushAsync`:

1. `SqliteConnection.ClearAllPools()` — releases all pooled connections to
   the file so Windows allows the copy.
2. Run `PRAGMA wal_checkpoint(TRUNCATE)` on a `Pooling=False` connection
   — ensures no WAL sidecar files need to be copied.
3. Atomic file copy to `CODEMAP_CACHE_DIR/{repoId}/{sha}.db`.
4. Skip if the target file already exists AND passes the SQLite magic header
   validation (idempotent + self-healing for corrupt entries).

Cache errors are logged as warnings but never surfaced to the caller. The
agent receives a successful response regardless of cache push outcome.

---

## Walkthrough: graph.trace_feature (Intent Navigation)

`graph.trace_feature` is the highest-level query — it builds a call tree
from an entry point annotated with architectural facts.

### 1. Validate entry point

`GraphHandler.HandleTraceFeatureAsync` accepts either:
- An FQN symbol_id: `"M:MyApp.Api.OrdersController.CreateOrder(CreateOrderRequest)"`
- A stable_id (sym_ prefix): `"sym_a1b2c3d4e5f6g7h8"`

If a stable_id is provided, `GetSymbolByStableIdAsync` resolves it to a
`SymbolId` first.

### 2. BFS callees traversal (GraphTraverser)

```csharp
var traversed = await _graphTraverser.TraverseCalleesAsync(
    routing, entryPoint, depth, limit, ct);
```

`GraphTraverser.TraverseCalleesAsync` runs a BFS starting from `entryPoint`:

```
Queue: [entryPoint]
Visited: {}

For each node:
  1. Add to visited
  2. expandNode(node) → GetOutgoingReferencesAsync(routing, node, ...)
     filters to ref_kind = 'Call'
     queries: SELECT * FROM refs WHERE from_symbol_id = $node LIMIT $clampedLimit+1
  3. Children added to queue (up to depth limit, total node limit)
```

The `+1` probe (querying `clampedLimit + 1` rows) lets the traverser detect
when more children exist without fetching them all, so `Truncated` in the
response is accurate.

### 3. Batch-fetch facts

```csharp
var allSymbolIds = traversed.Select(n => n.SymbolId).ToList();
var facts = await _store.GetFactsForSymbolsAsync(routing, allSymbolIds, ct);
```

Single SQL query for all traversed node facts — one round-trip regardless
of tree depth. Facts are keyed by `symbol_id` for O(1) lookup in step 5.

### 4. Build recursive tree

```csharp
var tree = BuildTree(entryPoint, traversed, factsById, depth: 0);
```

`BuildTree` is recursive. For each `TraversedNode`, it looks up children
via `node.ConnectedIds` (populated by `GraphTraverser` from the BFS edges)
and recurses. The result is a `TraceNode` tree with nested `Children`.

### 5. Annotate each node with facts

Each `TraceNode` gets its `Annotations` from the pre-fetched facts:

```csharp
new TraceAnnotation(
    Kind: fact.Kind.ToString(),     // e.g., "Route", "DbTable"
    Value: ParseDisplayValue(fact.Value),  // strips pipe metadata: "GET /orders" from "GET /orders|Route"
    Confidence: fact.Confidence)
```

`ParseDisplayValue` splits on the first `|` and returns the left side.
Route facts have no pipe, so the full value is returned unchanged.

### 6. Entry point route annotation

If the entry point has a `Route` fact, its route is also surfaced as
`FeatureTraceResponse.EntryPointRoute` — a top-level field for quick access
without scanning `Nodes[0].Annotations`.

### 7. Response

```csharp
return new FeatureTraceResponse(
    EntryPoint: entryPoint,
    EntryPointName: "CreateOrder",
    EntryPointRoute: "POST /api/orders",
    Nodes: [rootNode],          // hierarchical tree
    TotalNodesTraversed: 12,
    Depth: 3,
    Truncated: false);
```

---

## Data Model Quick Reference

All data lives in SQLite databases under `~/.codemap/`.

```
~/.codemap/
  baselines/
    {repoId}/
      {commitSha}.db    ← one DB per (repo, commit)
  overlays/
    {repoId}/
      {workspaceId}.db  ← one DB per workspace
  logs/
    codemap-{date}.log  ← daily log file (JSON lines)
  _savings.json         ← token savings accumulated since install
  config.json           ← user configuration
```

### Baseline Schema (per commit)

**`files`** — one row per source file in the compilation.

| Column         | Type    | Notes                                    |
|----------------|---------|------------------------------------------|
| `file_id`      | TEXT PK | Repo-relative forward-slash path         |
| `project_name` | TEXT    | MSBuild project name                     |
| `content_hash` | TEXT    | SHA-256 of file content at index time    |

**`symbols`** — one row per named symbol (class, method, property, field, event, etc.).

| Column        | Type    | Notes                                         |
|---------------|---------|-----------------------------------------------|
| `symbol_id`   | TEXT PK | FQN in documentation comment ID format (`M:Ns.Class.Method(T)`) |
| `stable_id`   | TEXT UQ | Nullable; SHA-256 fingerprint with sym_ prefix (SSID, ADR-021) |
| `display_name`| TEXT    | Human-readable name                           |
| `symbol_kind` | TEXT    | Class, Method, Property, Field, Event, etc.   |
| `namespace`   | TEXT    | Dotted namespace                              |
| `signature`   | TEXT    | Full signature string                         |
| `file_id`     | TEXT FK | Foreign key to files                          |
| `loc_start`   | INT     | 1-based line number                           |
| `loc_end`     | INT     | 1-based end line number                       |
| `project_name`| TEXT    | Denormalized from files for fast filtering    |

**`symbols_fts`** — FTS5 virtual table over `display_name`, `symbol_id`,
`namespace`, `signature`. Kept in sync via `INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')` after bulk inserts. Bare `*` wildcard not supported — use prefix like `Order*`.

**`refs`** — reference edges between symbols.

| Column               | Type  | Notes                                             |
|----------------------|-------|---------------------------------------------------|
| `ref_id`             | TEXT PK |                                                 |
| `from_symbol_id`     | TEXT  | Calling symbol (no FK — syntactic refs use simplified format) |
| `to_symbol_id`       | TEXT  | Called symbol (empty string for unresolved)       |
| `to_name`            | TEXT  | Method name for unresolved edges                  |
| `to_container_hint`  | TEXT  | Receiver expression for unresolved edges          |
| `ref_kind`           | TEXT  | Call, Read, Write, Instantiate, Override, Implementation |
| `file_id`            | TEXT FK |                                                 |
| `loc_start`          | INT   | Line of the reference                             |
| `resolution_state`   | TEXT  | `resolved` (default) or `unresolved`              |

**`type_relations`** — inheritance and interface implementation edges.

| Column            | Type  | Notes                                        |
|-------------------|-------|----------------------------------------------|
| `type_symbol_id`  | TEXT  | The derived/implementing type                |
| `related_symbol_id`| TEXT | The base class or interface                  |
| `relation_kind`   | TEXT  | `Base` or `Interface`                        |
| `display_name`    | TEXT  | Human-readable name of the related type      |
| `stable_type_id`  | TEXT  | Nullable stable_id of the derived type       |
| `stable_related_id`| TEXT | Nullable stable_id of the related type       |

System.Object is excluded from base type extraction — every class inherits
it, storing it would add noise without value.

**`facts`** — architectural facts attached to symbols.

| Column      | Type  | Notes                                             |
|-------------|-------|---------------------------------------------------|
| `fact_id`   | TEXT PK |                                                 |
| `symbol_id` | TEXT  | The symbol the fact belongs to (no FK — baseline symbols can have overlay facts) |
| `stable_id` | TEXT  | Nullable stable_id of the owning symbol           |
| `fact_kind` | INT   | FactKind enum: Route=0, Config=1, DbTable=2, DiRegistration=3, Middleware=4, RetryPolicy=5 |
| `value`     | TEXT  | `display|metadata` format (pipe-separated)        |
| `file_id`   | TEXT FK |                                                 |
| `loc_start` | INT   |                                                   |
| `confidence`| INT   | Confidence.High=0, Medium=1, Low=2               |

**`repo_meta`** — single-row metadata for the index.

| Column          | Type | Notes                              |
|-----------------|------|------------------------------------|
| `semantic_level`| INT  | Full=0, Partial=1, SyntaxOnly=2    |
| `indexed_at`    | TEXT | ISO 8601 timestamp                 |
| `symbol_count`  | INT  |                                    |
| `ref_count`     | INT  |                                    |

### Overlay Schema (per workspace)

Same tables as baseline, plus:

**`deleted_symbols`** — tracks symbols deleted from modified files.

| Column      | Type | Notes                   |
|-------------|------|-------------------------|
| `symbol_id` | TEXT | FQN of the deleted symbol |
| `stable_id` | TEXT | Nullable stable_id      |

**`overlay_meta`** — workspace metadata.

| Column          | Type | Notes                                  |
|-----------------|------|----------------------------------------|
| `workspace_id`  | TEXT |                                        |
| `base_commit_sha`| TEXT | The baseline commit this overlay is based on |
| `revision`      | INT  | Incremented on each `refresh_overlay`  |
| `semantic_level`| INT  | Level of the overlay's own extraction  |

---

## Key Design Decisions

All decisions are recorded in [DECISIONS.MD](DECISIONS.MD) with full
context and rationale. The table below summarizes the most impactful ones.

| ADR | Decision | Why |
|-----|----------|-----|
| ADR-001 | Pinned NuGet versions only | Central Package Management; floating wildcards cause build inconsistencies |
| ADR-002 | No `CodeMapResult<T>` alias | C# 12 doesn't support open generic using-aliases |
| ADR-010 | `using CmKind = CodeMap.Core.Enums.SymbolKind` in Roslyn code | `SymbolKind` exists in both `Microsoft.CodeAnalysis` and `CodeMap.Core.Enums` |
| ADR-015 | Hand-rolled stdio JSON-RPC | No stable .NET MCP SDK existed at implementation time; swapping later is transport-only |
| ADR-016 | `repoRootPath` on `CreateBaselineAsync` | `GetFileSpanAsync` reads from disk; path must be passed through the interface |
| ADR-017 | No bare `*` in FTS5 queries | SQLite FTS5 limitation; use prefix patterns or `GetSymbolsByFileAsync` |
| ADR-018 | `BaselineExistsAsync` checks file existence, not symbol count | An empty project (zero symbols) is a valid, complete baseline |
| ADR-020 | Three-mode SemanticLevel (Full/Partial/SyntaxOnly) | Agents need quality signal to decide whether to trust results or force re-index |
| ADR-021 | Stable Symbol Identity (SSID) with SHA-256 fingerprint | FQNs change on rename; stable_id survives renames for cross-session tracking |
| ADR-022 | `resolution_state` column on refs | Self-healing architecture: syntactic fallback produces unresolved edges that are upgraded when compilation improves |
| ADR-024 | Overlay facts table has no FK on `symbol_id` | Overlay facts can annotate baseline symbols (same rationale as refs table — ADR-011) |

For the complete decision history including context, alternatives
considered, and consequences, read [DECISIONS.MD](DECISIONS.MD).
