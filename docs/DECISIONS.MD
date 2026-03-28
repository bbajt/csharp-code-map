# DECISIONS.MD

## CodeMap — Architecture Decision Records

Append-only log. Each entry records a deviation from spec or a design choice
that affects subsequent phases. Entries are numbered sequentially.

---

## ADR-001: Pinned NuGet Versions Instead of Floating Wildcards

**Date:** 2026-03-04
**Phase:** PHASE-01-01, Task T01
**Status:** Accepted

**Context:**
PHASE-01-01 spec and MILESTONE.MD specified floating wildcard versions in
`Directory.Packages.props` (e.g., `Version="4.12.*"`). NuGet Central Package
Management (CPM) does not support floating versions — restore fails with
NU1011 error.

**Decision:**
Use exact pinned versions in `Directory.Packages.props`. The spec's
`RestorePackagesWithLockFile=true` was also removed since lock files are
redundant when versions are already pinned.

**Pinned versions (Milestone 01):**

| Package | Version |
|---------|---------|
| Microsoft.CodeAnalysis.CSharp.Workspaces | 4.12.0 |
| Microsoft.Build.Locator | 1.7.0 |
| LibGit2Sharp | 0.30.0 |
| Microsoft.Data.Sqlite | 9.0.2 |
| Microsoft.Extensions.Hosting | 9.0.2 |
| Microsoft.Extensions.Logging.Abstractions | 9.0.2 |
| xunit | 2.9.3 |
| FluentAssertions | 7.0.0 |
| Verify.Xunit | 28.9.0 |
| NSubstitute | 5.3.0 |
| BenchmarkDotNet | 0.14.0 |

**Impact on future phases:**
All PHASE files should specify exact versions if they add packages. The
`Directory.Packages.props` section in PHASE-01-01 is superseded by the
actual file in the repo.

---

## ADR-002: No Generic Using Alias for CodeMapResult\<T>

**Date:** 2026-03-04
**Phase:** PHASE-01-01, Task T02
**Status:** Accepted

**Context:**
API-SCHEMA.MD Section 2.7 defined a convenience alias:
```csharp
using CodeMapResult<T> = Result<T, CodeMapError>;
```

Open generic type aliases in `global using` statements are not supported in
C# 12 / .NET 9 (errors CS1002, CS1525). This is a known language limitation
— generic using aliases are only supported for closed generic types.

**Decision:**
Use the full type `Result<T, CodeMapError>` everywhere. Specifically,
`IQueryEngine` methods return `Result<ResponseEnvelope<T>, CodeMapError>`
directly.

**Impact on future phases:**
- Do NOT use `CodeMapResult<T>` in any PHASE file or code.
- Use `Result<T, CodeMapError>` explicitly.
- API-SCHEMA.MD Section 2.7 convenience alias is documentation-only,
  not compilable code. Agents should ignore it.
- Revisit if a future C# version adds open generic alias support.

---

## ADR-003: Explicit Constructors for Validated Records

**Date:** 2026-03-04
**Phase:** PHASE-01-01, Task T02
**Status:** Accepted

**Context:**
PHASE-01-01 spec used a compact primary constructor validation syntax for
records like `EvidencePointer`, `BudgetLimits`, and `RoutingContext`:

```csharp
public record BudgetLimits(int MaxResults = 20, ...)
{
    public BudgetLimits   // Compact validation body
    {
        if (MaxResults <= 0) throw ...;
    }
}
```

This syntax did not compile (CS1519). The positional record primary
constructor body syntax for validation is not supported in the form shown.

**Decision:**
Records that need constructor validation use explicit constructors with
parameter validation and `init` properties, or use a separate `Validate()`
method called from a factory. The exact implementation pattern chosen by the
T02 agent is the canonical pattern for this project going forward.

**Impact on future phases:**
- When a PHASE file shows record validation in the compact body form,
  agents should implement it as an explicit constructor instead.
- The record field names and types remain canonical — only the validation
  mechanism differs.

---

## ADR-004: Verify.Xunit UseDirectory Not Available Globally

**Date:** 2026-03-04
**Phase:** PHASE-01-01, Task T03
**Status:** Accepted

**Context:**
PHASE-01-01 spec defined a `ModuleInitializer.cs` with:
```csharp
VerifierSettings.UseDirectory("Snapshots");
```

In Verify.Xunit 28.x, `UseDirectory` is a per-test method on `SettingsTask`,
not a global setting on `VerifierSettings`.

**Decision:**
Global module initializer uses `ScrubLinesContaining` only. Per-test snapshot
directory configuration is handled at the test level when needed.

**Impact on future phases:**
When using Verify snapshots, set the directory per-test or per-class, not globally.

---

## ADR-005: TestUtilities Project Does Not Reference xunit Directly

**Date:** 2026-03-04
**Phase:** PHASE-01-01, Task T03
**Status:** Accepted

**Context:**
The `CodeMap.TestUtilities` project was initially set up with xunit package
references. This caused the dotnet test runner to treat it as a test assembly
and attempt to run it, resulting in testhost failures (no tests to discover).

**Decision:**
`CodeMap.TestUtilities` references only the production packages it needs
(Verify.Xunit for the module initializer). It does NOT reference xunit, the
test SDK, or the runner. Test projects that consume TestUtilities already
have these references.

**Impact on future phases:**
If adding shared test utilities that need xunit types (e.g., `[Fact]`,
`ITestOutputHelper`), use `<PrivateAssets>all</PrivateAssets>` to prevent
the project from being discovered as a test assembly, or keep xunit-dependent
utilities in a separate helper class within each test project.

**Validated in PHASE-01-02:** Adding LibGit2Sharp to TestUtilities (for
`TempGitRepo`) did NOT cause testhost detection issues. Production package
references in TestUtilities are safe — only test runner packages are dangerous.

---

## ADR-006: Solution Contains 16 Projects (Not 15)

**Date:** 2026-03-04
**Phase:** PHASE-01-01, Task T01
**Status:** Accepted

**Context:**
AC-T01-03 stated "Solution contains exactly 7 src projects and 8 test
projects (15 total)." However, Task T03 defined `CodeMap.TestUtilities`
as a shared test helper project, bringing the total to 16.

**Decision:**
16 projects is correct: 7 src + 8 test + 1 test utilities = 16.
AC-T01-03 was undercounted. The TestUtilities project is NOT a test project
(no tests execute from it) but lives under `/tests/` by convention.

**Impact on future phases:**
Project count is 16. Any future acceptance criteria counting projects
should use this number.

---

## ADR-007: Read References Deferred to Milestone 02

**Date:** 2026-03-04
**Phase:** PHASE-01-03, Task T03
**Status:** Accepted

**Context:**
The spec defined 6 RefKind values: Call, Read, Write, Instantiate, Override,
Implementation. The agent implemented 5 and deferred Read references, citing
performance cost (walking every IdentifierNameSyntax) and that the other 5
cover primary agent use cases.

**Decision:**
Accept for Milestone 01. Read references (property/field reads in non-write,
non-call contexts) are deferred. The `RefKind.Read` enum value exists in Core
but no extractor produces it yet.

**Impact on future phases:**
- PHASE-01-04 (storage): `refs` table schema unchanged, just won't contain Read rows.
- PHASE-01-05 (query engine): `refs.find` with `kind=Read` filter will return empty.
- Milestone 02: Implement Read reference extraction, possibly with performance
  optimization (batch per-document, skip trivial reads).

---

## ADR-008: IndexStats Schema — Actual vs Spec

**Date:** 2026-03-04
**Phase:** PHASE-01-03, Task T01
**Status:** Accepted — needs reconciliation

**Context:**
API-SCHEMA.MD Section 6.2 defined `IndexStats` with fields:
`SymbolCount, ReferenceCount, FileCount, ProjectCount, FactCount,
CompilationErrorCount, FailedProjects`.

The actual `IndexStats` record created in PHASE-01-01 has:
`SymbolCount, ReferenceCount, FileCount, ElapsedSeconds, Confidence`.

Missing: `ProjectCount`, `FactCount`, `CompilationErrorCount`, `FailedProjects`.
Added (not in spec): `ElapsedSeconds`, `Confidence`.

**Decision:**
The PHASE-01-01 implementation is the current reality. The RoslynCompiler
works with the actual type. The missing fields should be added to `IndexStats`
in a future phase (likely PHASE-01-04 or PHASE-01-06) when the MCP response
needs them.

**Impact on future phases:**
- PHASE-01-04: Store whatever `IndexStats` fields exist. Don't assume fields
  from API-SCHEMA.MD that aren't in the actual record.
- PHASE-01-06: When building MCP responses, check actual `IndexStats` fields.
  Add missing fields (ProjectCount, FailedProjects) to the record if needed
  for the `index.ensure_baseline` response.
- API-SCHEMA.MD should be updated to match reality once reconciled.

---

## ADR-009: Additional NuGet Package — Workspaces.MSBuild

**Date:** 2026-03-04
**Phase:** PHASE-01-03, Task T01
**Status:** Accepted

**Context:**
`Microsoft.CodeAnalysis.Workspaces.MSBuild` is a separate package from
`Microsoft.CodeAnalysis.CSharp.Workspaces`. It provides `MSBuildWorkspace`
which is required to load `.sln` and `.csproj` files. The original package
list in PHASE-01-01 did not include it.

**Decision:**
Package added to `Directory.Packages.props` at version 4.12.0. This is
a required dependency — without it, solution loading is impossible.

**Impact on future phases:**
Package list is now 7 production packages (was 6). The allowed package
table in PHASE-01-01 is superseded by the actual `Directory.Packages.props`.

---

## ADR-010: Roslyn Namespace Alias Pattern

**Date:** 2026-03-04
**Phase:** PHASE-01-03, Tasks T02/T03
**Status:** Accepted

**Context:**
`Microsoft.CodeAnalysis.SymbolKind` and `Microsoft.CodeAnalysis.RefKind`
conflict with `CodeMap.Core.Enums.SymbolKind` and `CodeMap.Core.Enums.RefKind`.
Both are used extensively in the Roslyn extraction code.

**Decision:**
Use `using` aliases at file level:
```
using CmKind = CodeMap.Core.Enums.SymbolKind;
using CodeMapRefKind = CodeMap.Core.Enums.RefKind;
```

**Impact on future phases:**
Any code in `CodeMap.Roslyn` that references both Roslyn types and CodeMap
enums should follow this alias convention. Consistent naming helps readability.

---

## ADR-011: No Symbol FK Constraints on refs Table

**Date:** 2026-03-04
**Phase:** PHASE-01-04, Task T01/T02
**Status:** Accepted

**Context:**
The `refs` table schema in SYSTEM-ARCHITECTURE.MD included FK constraints on both
`from_symbol_id` and `to_symbol_id` referencing `symbols(symbol_id)`. During testing,
inserting refs where the `from_symbol_id` (e.g., `SampleApp.Program`) was not in the
`symbols` table failed with a SQLite error. SQLite's `INSERT OR IGNORE` conflict resolution
does NOT suppress FK violations (it only handles UNIQUE and NOT NULL constraints).

**Decision:**
Remove FK constraints from `refs(from_symbol_id)` and `refs(to_symbol_id)`. Keep
`refs(file_id)` FK to `files(file_id)` since files are always local.

External symbol references (cross-project calls to types not compiled in the solution,
framework method calls, etc.) are valid and common in Roslyn analysis. Blocking them with
FK constraints would prevent storing legitimate references.

**Impact on future phases:**
- Refs table stores any symbol ID as from/to. Query engine must handle missing symbol
  lookups gracefully (symbol in refs but not in symbols = external/unknown symbol).
- PHASE-01-05: `GetReferencesAsync` may return `StoredReference.FromSymbol` values that
  don't resolve in `GetSymbolAsync` — this is expected for external references.

---

## ADR-012: BaselineStore.CreateBaselineAsync Extended with repoRootPath

**Date:** 2026-03-04
**Phase:** PHASE-01-04, Task T02/T03
**Status:** Accepted

**Context:**
`ISymbolStore.GetFileSpanAsync` receives a `FilePath` (repo-relative) and must read
file content from disk. The interface has no mechanism to resolve relative paths to
absolute paths. The storage layer needs the repo root to compute the absolute path.

**Decision:**
`BaselineStore.CreateBaselineAsync` adds an optional `string repoRootPath = ""` parameter.
When non-empty, it is stored in a `repo_meta` table (`key='repo_root'`). `GetFileSpanAsync`
reads this value to resolve absolute paths. The `ISymbolStore` interface is preserved via
explicit interface implementation (4-parameter version delegates to 5-parameter version with
empty `repoRootPath`).

**Impact on future phases:**
- PHASE-01-05 (Query Engine): When calling `ISymbolStore.GetFileSpanAsync`, ensure
  `repoRootPath` was set at index time. Pass `repoRootPath` to `BaselineStore.CreateBaselineAsync`
  (not `ISymbolStore.CreateBaselineAsync`) in the daemon wiring.
- PHASE-01-06 (Daemon DI): Wire `BaselineStore` directly (not `ISymbolStore`) in the
  composition root to pass `repoRootPath` parameter.

---

## ADR-013: ResponseMeta Actual Fields Differ from PHASE-01-05 Spec

**Date:** 2026-03-04
**Phase:** PHASE-01-05, T01
**Status:** Accepted

**Context:**
PHASE-01-05 spec described `ResponseMeta` assembly with fields:
`WorkspaceId`, `OverlayRevision`, `CacheHit`, `TokensSavedTotal`, `CostAvoidedTotal`.

The actual `ResponseMeta` record created in PHASE-01-01 has only:
`Timing`, `BaselineCommitSha`, `LimitsApplied`, `TokensSaved`, `CostAvoided`.

`CostAvoided` is a single `decimal` (not a per-model dictionary).

**Decision:**
Use the actual record. `CostAvoided` is populated using the claude_sonnet rate
(0.003 per 1K tokens). `TokenSavingsEstimator.EstimateCostPerModel` returns a
per-model dict used only with `ITokenSavingsTracker.RecordSaving`.

**Impact on future phases:**
- PHASE-01-06: When serializing MCP responses, use actual `ResponseMeta` fields.
  Missing spec fields (`CacheHit`, `TokensSavedTotal`) can be added to `ResponseMeta`
  if needed for MCP response contract.

---

## ADR-014: GetDefinitionSpanAsync Cache Check Before Symbol Store Call

**Date:** 2026-03-04
**Phase:** PHASE-01-05, T02
**Status:** Accepted

**Context:**
The phase spec ordered `GetDefinitionSpanAsync` as: resolve commit → get symbol →
compute span → check cache. This means the store is called even on cache hits.

**Decision:**
Move cache check BEFORE `GetSymbolAsync`. The cache key is deterministic from
`{repoId}:{commitSha}:defspan:{symbolId}:{maxLines}:ctx{contextLines}` and does
not require knowing span boundaries. This avoids a store round-trip on cache hits.

**Impact on future phases:**
No impact — internal implementation detail. External behavior unchanged.

---

_End of current entries. Append new ADRs below._

---

## ADR-015: Hand-Rolled stdio MCP Transport (No MCP SDK)

**Date:** 2026-03-04
**Phase:** PHASE-01-06, Task T01
**Status:** Accepted

**Context:**
The phase spec assumed an MCP SDK package would be available. No stable .NET MCP SDK
existed at implementation time. Evaluated ModelContextProtocol.AspNetCore and similar
packages — all pre-release or incompatible with stdio transport requirements.

**Decision:**
Hand-rolled stdio JSON-RPC 2.0 transport with Content-Length framing. Implemented
`McpDispatcher`, `ToolRegistry`, `ToolDefinition`, and `StdioTransport` from scratch.
This is ~300 lines of straightforward code that fully satisfies the MCP protocol.

**Impact on future phases:**
- MCP handlers in `CodeMap.Mcp` follow the hand-rolled pattern.
- Adding new tools: implement handler + register in `ToolRegistry`.
- No SDK upgrade path needed (we own the implementation).

---

## ADR-016: ISymbolStore.CreateBaselineAsync Extended with repoRootPath Parameter

**Date:** 2026-03-04
**Phase:** PHASE-01-06, Task T02
**Status:** Accepted

**Context:**
The `IndexHandler` (in CodeMap.Mcp) injects `ISymbolStore` (not concrete `BaselineStore`)
per dependency rules. However, `GetFileSpanAsync` needs the repo root path to resolve
relative paths. The path must be stored at index time.

**Decision:**
Added `string repoRootPath = ""` optional parameter to `ISymbolStore.CreateBaselineAsync`.
The default keeps the interface backward-compatible. `BaselineStore` stores the value in
`repo_meta` table. Integration tests and the daemon DI root pass `repoRootPath` explicitly.

**Impact on future phases:**
- All callers of `CreateBaselineAsync` should pass `repoRootPath` when the repo root is known.
- The overlay equivalent does not need a `repoRootPath` — it delegates file reads to the baseline.

---

## ADR-017: ISymbolStore.GetSymbolsByFileAsync — New Method for Incremental Diff

**Date:** 2026-03-04
**Phase:** PHASE-02-01, Task T04
**Status:** Accepted

**Context:**
PHASE-02-01 spec described `SymbolDiffer.ComputeDelta` querying the baseline with
`SearchSymbolsAsync(query: "*", FilePath: file)`. SQLite FTS5 does not support bare `*`
as a match-all wildcard (produces `'unknown special query: *'` error). The existing
`SearchSymbolsAsync` always routes through FTS5.

**Decision:**
Added `GetSymbolsByFileAsync(RepoId, CommitSha, FilePath, CancellationToken)` to
`ISymbolStore`. This performs a direct SQL `JOIN files f ON s.file_id = f.file_id WHERE
f.path = $path` — no FTS involvement. Also updated the contract test count from 6 → 7.

**Impact on future phases:**
- `SymbolDiffer` uses this method for deletion detection.
- Future `IOverlayStore` read path uses a similar direct query pattern.
- API-SCHEMA.MD does not document this method (internal engine interface).

---

## ADR-018: BaselineExistsAsync Checks DB File Existence, Not Symbol Count

**Date:** 2026-03-04
**Phase:** PHASE-02-02, Task T03
**Status:** Accepted

**Context:**
`BaselineStore.BaselineExistsAsync` originally queried `SELECT COUNT(*) FROM symbols LIMIT 1`
and returned true only when at least one symbol existed. This blocked `WorkspaceManager.CreateWorkspaceAsync`
from succeeding on empty compilations (projects with no extractable symbols), causing integration
tests using `MakeEmptyCompilation()` to fail with `INDEX_NOT_AVAILABLE`.

**Decision:**
Changed `BaselineExistsAsync` to return true if and only if the baseline DB file exists
(`_factory.OpenExisting(repoId, commitSha) is not null`). An empty indexed project is a
valid baseline — workspace creation should succeed regardless of symbol count.

**Impact:**
- Unit test `BaselineExists_EmptyDb_ReturnsFalse` renamed to `BaselineExists_EmptyDb_ReturnsTrue`.
- Integration tests using `MakeEmptyCompilation()` as baseline seed now work correctly.
- Semantically correct: the baseline represents "this commit was indexed", not "this commit had symbols".

---

## ADR-019: ResponseMeta Extended with Optional WorkspaceId and OverlayRevision Fields

**Date:** 2026-03-04
**Phase:** PHASE-02-03, Task T01
**Status:** Accepted

**Context:**
PHASE-02-03 spec requires workspace-mode responses to include workspace context in `ResponseMeta`
so agents can distinguish workspace vs. committed results. The existing `ResponseMeta` record had
only 5 fields (Timing, BaselineCommitSha, LimitsApplied, TokensSaved, CostAvoided).

**Decision:**
Added two optional fields at the end of `ResponseMeta`:
- `WorkspaceId? WorkspaceId = null` — null for committed mode
- `int OverlayRevision = 0` — 0 for committed mode

Optional (default) params are backward-compatible with existing positional and named callers.
`EnvelopeBuilder.Build` updated with matching optional params. No breaking changes.

**Impact:**
- All committed-mode envelope builds continue to work without modification.
- `MergedQueryEngine` passes workspace context to `EnvelopeBuilder.Build` in workspace mode.
- JSON serialization of `ResponseMeta` now includes `workspaceId` and `overlayRevision` fields.

---

## ADR-020: SemanticLevel Enum and Three-Mode Pipeline

**Date:** 2026-03-05
**Phase:** PHASE-02-08, Task T01
**Status:** Accepted

**Context:**
When compilation fails for some projects, agents receive mixed-quality data (High confidence from
compiled projects, Low confidence from syntactic fallback) with no indication of the overall index
quality. Agents cannot distinguish "zero references" from "references not extracted due to failure".

**Decision:**
Added `SemanticLevel` enum with three values:
- `Full` — all projects compiled successfully
- `Partial` — some projects compiled, some fell back to syntax-only
- `SyntaxOnly` — no projects compiled (all data from AST)

`SemanticLevel` is computed per-compilation in `RoslynCompiler.ExtractSolutionAsync` by counting
compiled vs. exception-throwing projects. Stored in baseline `repo_meta` and overlay `overlay_meta`
key-value tables. Propagated to `ResponseMeta.SemanticLevel` in every query response.

`IndexStats` and `CompilationResult` extended with `SemanticLevel` and `ProjectDiagnostics` fields
(optional, defaulted for backward compatibility). `QueryEngine` reads level from store per-query.
`MergedQueryEngine` merges as `min(baseline, overlay)` (worst quality wins).

**Key distinction:** Projects with compilation errors but usable `SemanticModel` count as
`Compiled: true`. Only projects where `GetCompilationAsync` throws an exception are `Compiled: false`.

**Impact:**
- Every response includes `SemanticLevel` in `ResponseMeta`.
- `ProjectDiagnostic` records available for per-project quality inspection.
- Agents can detect degraded mode and communicate it to users.

---

## ADR-021: Stable Symbol Identity Deferred to PHASE-03-00

**Date:** 2026-03-05
**Phase:** PHASE-02-08 (MODIFIED from RECONCILIATION-01.MD)
**Status:** Accepted

**Decision (modified from RECONCILIATION-01.MD):**
SSID implementation is deferred to PHASE-03-00 (first phase of Milestone 03), not PHASE-02-08 T02
as originally proposed. The public MCP API continues to use FQN-based `symbol_id`. All PHASE-03
table schemas must include a `stable_id TEXT` nullable placeholder column. The overlay merge key
remains FQN-based until PHASE-03-00 completes.

**Rationale for deferral:**
1. SSID touches every layer (extraction, storage, query, merge, cache, MCP API)
2. Fingerprint algorithm correctness requires testing against larger codebases
3. Getting fingerprinting wrong (collisions, instability) is worse than FQN
4. A dedicated phase gives SSID full attention rather than competing with SemanticLevel work
5. M03 tables are designed with `stable_id` placeholder from inception, keeping migration cost linear

**Impact:**
- PHASE-03-00: implement SymbolFingerprinter, add stable_id column, migrate merge key
- PHASE-03-01+: all new tables join on `stable_id` (nullable until PHASE-03-00, then NOT NULL)
- MergedQueryEngine merge key migration: PHASE-03-00

---

## ADR-022: Resolution State on Reference Edges

**Date:** 2026-03-05
**Phase:** PHASE-02-08, Task T02
**Status:** Accepted

**Context:**
When compilation fails, `ReferenceExtractor` produces nothing (requires SemanticModel). Agents
querying `refs.find` on a broken codebase receive empty results, unable to distinguish "no references"
from "references not extracted".

**Decision:**
Added three columns to the `refs` table:
- `resolution_state TEXT NOT NULL DEFAULT 'resolved'`
- `to_name TEXT` — identifier name for unresolved edges (e.g. "Execute", "Log")
- `to_container_hint TEXT` — receiver/container text truncated to 100 chars (e.g. "_service")

Two resolution states:
- `resolved` — full SemanticModel resolution; `to_symbol_id` is the exact target FQN
- `unresolved` — syntactic extraction; `to_symbol_id` is empty string `""`

`SyntacticReferenceExtractor` walks the AST without a SemanticModel, producing unresolved edges
for invocations, object creations, and member accesses. Runs ONLY in the compilation fallback path.

`ExtractedReference`, `StoredReference`, `StoredOutgoingReference`, and `ClassifiedReference` all
extended with `ResolutionState`, `ToName`, `ToContainerHint` fields (optional, defaulted to
`Resolved` for backward compatibility).

`refs.find` accepts optional `resolution_state` filter. Default returns both resolved and unresolved.
`IResolutionWorker` interface stub defined — no implementation, no DI registration.

**Syntactic from_symbol_id format:** Uses simplified identifiers (`ClassName::MethodName`) that
differ from semantic FQN format (`M:Namespace.Class.Method(params)`). Resolution workers reconcile.

**Impact:**
- Broken codebases produce syntactic reference edges instead of empty results.
- Agents can filter by `resolution_state` to get only fully-resolved data.
- Refs table schema changed; new databases created with T02 schema. Old baselines use DEFAULT value.

---

## ADR-023: Intent-Level Navigation Deferred to Milestone 04

**Date:** 2026-03-05
**Phase:** PHASE-02-08
**Status:** Accepted

**Decision:**
Intent-level navigation (semantic jump targets, overload resolution, semantic diff) is deferred to
Milestone 04 as originally proposed in RECONCILIATION-01.MD. No changes to current MCP API.

**Rationale:**
The resilience foundation (SemanticLevel + Resolution State) must be complete and stable before
building higher-level navigation. Milestone 03 (semantic queries: callers/callees improvements,
better type hierarchy) comes first.

---

## ADR-024: Overlay facts Table Has No FK on symbol_id

**Date:** 2026-03-05
**Phase:** PHASE-04-06, Task T02
**Status:** Accepted

**Context:**
The overlay `facts` table initially had a FK constraint: `FOREIGN KEY (symbol_id) REFERENCES symbols(symbol_id)`.
During implementation of `E2E_TraceFeature_WorkspaceMode_IncludesOverlayFacts`, the integration test
failed with SQLite Error 19 (FOREIGN KEY constraint failed). The test applied overlay facts for a
baseline symbol (one that exists in the baseline DB, not in the overlay's `symbols` table). Since the
overlay symbols table only contains symbols re-indexed in the overlay, a fact annotating an existing
baseline symbol would always fail the FK check.

**Decision:**
Remove `FOREIGN KEY (symbol_id) REFERENCES symbols(symbol_id)` from the overlay `facts` table DDL in
`OverlaySchemaDefinition.cs`. Retain `FOREIGN KEY (file_id) REFERENCES files(file_id)` since overlay
files are always local to that workspace DB.

**Rationale:**
This follows the same pattern as ADR-011 (no FK on symbol columns in the `refs` table). Overlay facts
annotate symbols from any source — baseline or overlay — without requiring the annotated symbol to be
re-indexed in the overlay. The overlay store is a decoration layer, not a replacement for the baseline.

**Impact:**
- Overlay facts can reference any symbol ID, including baseline symbols not in the overlay's symbols table.
- Query engine must handle missing symbol lookups gracefully (same as for refs, per ADR-011).
- No change to read path — `GetOverlayFactsForSymbolAsync` queries by symbol_id directly.

## ADR-025: MCP stdio Transport Is Newline-Delimited JSON, Not Content-Length Framed

**Date:** 2026-03-06

**Context:**
The hand-rolled `McpServer.cs` was implemented with Content-Length framing (LSP style, as used by
protocol version `2024-11-05`). When Claude Code CLI v2.1.70 was used as the MCP client, the server
failed silently — the client would send messages and the server would never respond, eventually
exiting after the client closed stdin.

A transparent stdio proxy (Python) captured the actual bytes Claude Code sends:

```
{"method":"initialize","params":{"protocolVersion":"2025-11-25",...},"jsonrpc":"2.0","id":0}\n
```

**Discovery:**
Claude Code v2.1.70 uses MCP protocol version `2025-11-25` with **plain newline-delimited JSON**
(one JSON object per line, `\n` terminated). There are no `Content-Length:` headers.
The server's `ReadMessageAsync` was stuck in a header-reading loop waiting for a blank line that
never arrived, causing a ~30-second timeout before the connection was killed.

**Decision:**
Update `McpServer` to auto-detect the transport format from the first message:
- If the first line starts with `{`, use newline-delimited JSON for both read and write.
- If the first line starts with `Content-Length:`, use LSP-framed for both read and write.

This makes the server backwards-compatible with test harnesses that still use Content-Length framing
while correctly handling the format that production Claude Code clients send.

**Impact:**
- `ReadMessageAsync` signature changed to `Task<(string? Body, bool IsNewline)>`.
- `SendAsync` takes a `bool newlineDelimited` parameter.
- `RunAsync` latches the format on the first received message.
- `McpSubprocessTests` updated to use newline-delimited JSON (matching Claude Code behaviour).

---

## ADR-026: Upgrade Roslyn to 5.3.0 for .slnx support

**Date:** 2026-03-13
**Status:** Accepted

**Context:**
Users with .NET SDK 9.0.200+ projects use the new XML-based .slnx solution
format. MSBuildWorkspace.OpenSolutionAsync in Roslyn 4.x throws
InvalidProjectFileException ("No file format header found") on .slnx files
because MSBuild's SolutionFile.Parse() only handles the old text-based .sln
format. Observed in production log from a user running codemap-mcp against dm.slnx.

**Decision:**
Upgrade Microsoft.CodeAnalysis.CSharp.Workspaces and
Microsoft.CodeAnalysis.Workspaces.MSBuild from 4.12.0 to 5.3.0.
Roslyn PR #77326 added .slnx support via Microsoft.VisualStudio.SolutionPersistence.
No API changes required — OpenSolutionAsync works transparently with both .sln and .slnx.
Workspace.WorkspaceFailed event replaced with RegisterWorkspaceFailedHandler (obsolete in 5.x).

**Consequences:**
- .slnx solution files fully supported for all CodeMap operations.
- Full backward compatibility with all .sln formats (VS2010 through VS2022).
- Microsoft.VisualStudio.SolutionPersistence 1.0.52 added as transitive dependency.
- Query performance unchanged (Roslyn compilation not on query hot path).

---

## ADR-027: Lazy On-Miss DLL Symbol Resolution

**Date:** 2026-03-18
**Phase:** PHASE-12-01 through PHASE-12-03
**Status:** Accepted

**Context:**
CodeMap stopped cold at DLL boundaries. Symbols from MetadataReferences (referenced
assemblies) were absent from the symbols table, causing NOT_FOUND for any query that
followed a call edge into a DLL. This silently truncated graph traversals and
type hierarchies whenever an agent navigated past the application's own source code.

**Decision:**
Implement lazy on-miss DLL resolution (Option C from MILESTONE-12.MD). When any
query encounters a symbol absent from the DB that Roslyn can resolve from a loaded
MetadataReference, extract that type's metadata stub immediately, write it
permanently to the baseline DB, and serve the query — all transparently within the
same request.

Two resolution levels:
- Level 1 (always active): Roslyn `INamedTypeSymbol.GetMembers()` walk. No new
  dependencies. Produces method signatures, parameter names, XML doc comments,
  type hierarchy. `is_decompiled = 1`. Cost ~1–5ms per type (pure in-memory walk).
- Level 2 (on `include_code: true`): `ICSharpCode.Decompiler.DecompileType()`. Produces
  reconstructed C# source stored as a virtual file in the baseline DB.
  `is_decompiled = 2`. Cost ~10–200ms per type (one-time first-access cost).

After Level 2, `ReferenceExtractor` runs over the decompiled SyntaxTree to extract
outgoing call refs from the decompiled type's methods. These refs (marked
`is_decompiled = 1` in the refs table) enable `graph.callees` and `graph.trace_feature`
to traverse INTO and THROUGH DLL code.

**Rationale:**
DLLs are fully static once referenced — a given DLL at version X.Y.Z always
produces identical metadata. Indexing results can be cached permanently in the
baseline DB (not the overlay). The lazy model avoids all upfront cost at
`index.ensure_baseline` time, naturally scopes what gets indexed to what the agent
actually navigates, and scales to arbitrarily large DLL sets (BCL, framework
packages) without configuration.

**Consequences:**
- First access to a new DLL type has a small latency spike (Level 1 ~1–5ms;
  Level 2 ~10–200ms). All subsequent accesses are 0ms (DB hit).
- `graph.trace_feature` traversal depth may be limited by the `MaxLazyResolutions`
  budget (default 20 per query) when encountering many previously-unseen DLL types
  in one traversal. Budget exhaustion sets `Truncated = true` in the response.
- Baseline key (`repo_id`, `commit_sha`) is unchanged. `dll_fingerprint` field in
  baseline metadata handles DLL version change detection.
- `is_decompiled` column added to both `symbols` and `refs` tables (`DEFAULT 0` — no
  migration needed for existing baselines).
- Virtual decompiled source stored in the baseline DB `files` table (`is_virtual = 1`,
  `decompiled_source` column). `GetFileSpanAsync` routes virtual reads to DB content.
- `ICSharpCode.Decompiler` referenced only by `CodeMap.Roslyn`. No other project
  references it. Core zero-dependency constraint preserved.

---

## ADR-028: symbols.search query Parameter Made Optional — Kind-Browse Path

**Date:** 2026-03-28
**Version:** v1.3.1
**Status:** Accepted

**Context:**
Agents commonly invoke `symbols.search` with `query: "*"` to list all symbols
of a given kind (e.g. all classes in a project). FTS5 does not accept a bare
`*` as a match-all wildcard — it throws an internal SQLite error, which
surfaced to the MCP caller as `-32603 Internal error`. This is a well-known
agent UX failure: the intent is clear and valid, but the mechanism is wrong.

**Decision:**
1. `IQueryEngine.SearchSymbolsAsync` signature changed from `string query` to
   `string? query` (nullable). The storage-layer `ISymbolStore.SearchSymbolsAsync`
   is unchanged (still requires non-empty string).

2. `QueryEngine` dispatches based on query nullability:
   - `query` non-empty → FTS5 text search (existing path, unchanged)
   - `query` null/empty + `kinds` filter → `BrowseByKindsAsync` private method,
     which calls `ISymbolStore.GetSymbolsByKindsAsync` (direct SQL, no FTS)
   - `query` null/empty + no `kinds` → `INVALID_ARGUMENT` with guidance message

3. `MergedQueryEngine` applies the same dispatch before the workspace-mode path.
   For kind-browse in workspace mode, it delegates to the inner engine with a
   committed routing context (baseline only — no overlay merge for browse).

4. `McpToolHandlers.HandleSearchAsync`:
   - `query` removed from the JSON schema `required` array.
   - `query: "*"` is silently converted to `null` before dispatch.
   - Schema description updated to document both modes.

**Consequences:**
- Agents can pass `kinds: ["Class"]` with no `query` and get all classes.
- Agents passing `query: "*"` get the same result (silent coercion).
- `Score` in `SymbolSearchHit` is `1` for kind-browse results (no FTS ranking).
- `namespace` and `file_path` filters are NOT applied in the kind-browse SQL path
  (only `kinds` is used). Agents needing namespace-scoped kind browse should
  post-filter client-side or use a text query like `"*"` within the namespace.
- No interface change to `ISymbolStore` — `GetSymbolsByKindsAsync` already existed
  (added in PHASE-06-03 for `codemap.export`).
