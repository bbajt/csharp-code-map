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

## ADR-029: VB.NET Language Support via Parallel Extractor Classes

**Date:** 2026-04-01
**Status:** Accepted

**Context:**
Users requested VB.NET support for mixed C#/VB.NET solutions. Roslyn provides
language-agnostic `ISymbol` APIs, so the query layer requires zero changes. Only
the extraction layer — which uses C#-specific `SyntaxNode` types — needed adaptation.

**Decision:**
1. All VB.NET extraction code lives in `CodeMap.Roslyn/Extraction/VbNet/`.
2. Each C# extractor's `ExtractAll` dispatches to a parallel `VbXxxExtractor`
   when `compilation.Language == LanguageNames.VisualBasic`.
3. `VbSyntacticExtractor` handles degraded mode for VB.NET projects using
   `VisualBasicSyntaxWalker`.
4. A separate `testdata/SampleVbSolution/` fixture mirrors SampleSolution in VB.NET.
5. No new MCP tools. No `IQueryEngine` or `ISymbolStore` interface changes.

**Consequences:**
- VB.NET projects in mixed solutions are indexed transparently.
- All 26 MCP tools work for VB.NET symbols without agent changes.
- VB.NET-specific patterns (Modules, `Implements` keyword, VB property syntax,
  VB indexer `_config("key")`) are handled in the VbNet/ extractors without
  polluting C# extraction code.
- `VbLogExtractor` value format is `"Level|template"` (level-first); C# is
  `"template|Level"`. Similarly `VbRetryPolicyExtractor` uses `"framework|desc"`.
  These are intentional divergences for easier consumer filtering.
- Known limitation: raw SQL table extraction for VB.NET is not implemented —
  string literal regex is language-agnostic but the extractor uses C# syntax
  trees to locate call sites. Deferred to a future phase.

## ADR-030: File Content Storage in SQLite for code.search_text Performance

**Date:** 2026-04-02
**Status:** Accepted

**Context:**
`code.search_text` was scanning files from disk at query time. On large repos such as
dotnet/roslyn with 1,973 source files, this produced `ranking_ms = 636ms` — over 1,000
individual disk reads per query. VB.NET files had no special treatment and were included
in the disk scan, making the problem equally bad for mixed-language solutions.

**Decision:**
1. Add `content TEXT` column to the `files` table. Populated at index time by
   `BaselineStore.InsertFilesAsync` from `ExtractedFile.Content`.
2. Add `Content? string = null` field to `ExtractedFile` record. `RoslynCompiler.ExtractFiles`
   already called `File.ReadAllText` to compute the sha256 hash — that content is now passed
   through instead of being discarded.
3. Add `files_fts` external content FTS5 virtual table over `files.content`. Rebuilt in
   `BaselineStore.RebuildFtsAsync` alongside `symbols_fts` after bulk inserts.
4. Add `ISymbolStore.GetAllFileContentsAsync` returning `IReadOnlyList<(FilePath, string?)>`.
   SQL: `SELECT path, content FROM files ORDER BY path`.
5. `QueryEngine.SearchTextAsync` calls `GetAllFileContentsAsync` instead of
   `GetAllFilePathsAsync`. For entries with stored content, lines are split in-memory (no
   disk I/O). For entries with `content IS NULL` (old baselines), falls back to
   `File.ReadAllLinesAsync` for backward compatibility.

**Consequences:**
- `ranking_ms` drops from 636ms → 144ms on dotnet/roslyn (4.4× speedup). Remaining
  time is CPU-bound regex matching, not I/O.
- `db_query_ms` increases slightly (~72ms) to return file content in one SELECT.
- Old baselines without the `content` column are handled transparently via the null fallback.
- VB.NET and C# files are stored and searched identically — the content column is
  language-agnostic.

---

## ADR-031: v2 Custom Storage Engine — Key Design Decisions

**Date:** 2026-04-02 (Milestone 14, Phases 2–6)
**Status:** Accepted

**Context:**
The v2 custom storage engine replaces SQLite with memory-mapped binary segment files
for baseline storage and a WAL-backed overlay for workspace mutations. The engine is
a drop-in replacement at the `ISymbolStore` + `IOverlayStore` boundary — all code
above the storage layer (QueryEngine, MergedQueryEngine, MCP handlers) is unchanged.

**Key decisions made during implementation:**

1. **All engine-internal types are `internal` (not `public` per spec).**
   Record structs (SymbolRecord, EdgeRecord, etc.) and engine interfaces
   (IEngineBaselineReader, IEngineSearchIndex, etc.) are internal because
   `IEngineBaselineReader.GetSymbolByIntId` returns `ref readonly SymbolRecord`.
   A public interface returning an internal type is a compile error. Only
   `CustomSymbolStore`, `CustomEngineOverlayStore`, exception types, and
   `StorageTelemetry` are public.

2. **Degraded FQN-based StableId for Phase 3 (DD-1).**
   `SymbolCard` does not have discrete `ReturnType`, `ParamTypes[]`, `IsStatic`
   fields needed by the v2 fingerprint algorithm. Phase 3 uses a degraded
   fingerprint: SHA-256 of `Kind + FQN + ProjectName`. This produces unique
   deterministic IDs but loses rename-survival. Full semantic fingerprint
   deferred to a later phase when Roslyn extraction is extended.

3. **Overlay directories keyed by workspaceId (not commitSha).**
   Initial implementation keyed overlays by commitSha, which broke workspace
   isolation when multiple workspaces shared the same baseline commit. Fixed
   in Phase 6 review to key by workspaceId, with a separate `_wsToCommit`
   dictionary tracking baseline commit per workspace.

4. **Separate IntId counters for symbols, edges, files, facts in overlay.**
   Initially all overlay entities shared `NextOverlayEdgeIntId`. This caused
   ID space collisions (a FileRecord and EdgeRecord could have the same IntId).
   Fixed in Phase 6 review with dedicated `NextOverlayFileIntId` and
   `NextOverlayFactIntId` counters. Snapshot format bumped to v2.

5. **Contiguous packed records (no page headers) for baseline segments.**
   Per design review resolution C-003, baseline `.seg` files use contiguous
   packed record arrays directly after the 16-byte SegmentFileHeader. Page
   headers are reserved for WAL and overlay snapshot only. Record `i` is at
   byte offset `16 + i * sizeof(T)`.

6. **Graceful shutdown checkpoint (C-019).**
   `EngineOverlay.Dispose()` triggers a WAL checkpoint before releasing file
   handles. This ensures the overlay is clean for next session startup,
   making WAL recovery a no-op in the normal case.

7. **Pointer leak safety in mmap reader constructors.**
   All readers that call `AcquirePointer` wrap post-acquire initialization
   in try/catch. On exception, the pointer is released and all mmap handles
   are disposed before re-throwing. This prevents memory leaks when segment
   file headers fail validation.

**Consequences:**
- The custom engine is a complete `ISymbolStore` + `IOverlayStore` implementation
  with all 27 + 22 methods functional.
- Two code reviews (Phases 2-5 and Phase 6) found and fixed 10 critical + 8
  important issues before the engine reached production readiness.
- The feature branch `feature/v2-storage-engine` is ready for merge to `main`
  behind the `CODEMAP_ENGINE=custom` environment variable guard.
- `ISymbolStore` method count: 26 → 27. `InterfaceContractTests` updated accordingly.
- Database size grows proportionally to total source file size. For dotnet/roslyn
  VbPortable.sln (~5,907 file rows across 3 TFMs), this is acceptable. Compression
  deferred to a future milestone if needed.

---

## ADR-032: v2 Overlay Store Repo ID Routing Fix

**Date:** 2026-04-03
**Status:** Accepted

**Context:**
After swapping to the v2 engine binary (v2.0.0), `workspace.create` failed with
`"No baseline found for repo _default commit <sha>"`. The baseline existed under
the correct repo ID (`local-b73d0e677cfb93f6`) but the overlay store was looking
for it under `"_default"`.

**Root cause:**
`CustomSymbolStore.GetOrOpenBaseline(string commitSha)` hardcoded `"_default"` as
the repo ID and ignored the actual repo ID passed through `CreateOverlayAsync`.
The `_wsToCommit` dictionary stored only `commitSha` per workspace, discarding
the `repoId` argument.

**Fix:**
1. Changed `_wsToCommit` from `Dictionary<string, string>` to
   `Dictionary<string, (string CommitSha, string RepoId)>`.
2. Updated `GetOrOpenBaseline` to require an explicit `repoId` parameter
   (removed hardcoded `"_default"`).
3. Updated all four call sites in `CustomEngineOverlayStore` (`CreateOverlayAsync`,
   `ResetOverlayAsync`, `UpgradeOverlayEdgeAsync`, `GetOverlayAndReader`) to
   pass the stored repo ID through.

**Consequences:**
- Workspace creation works correctly for all repo ID formats.
- No interface changes — fix is entirely within the v2 engine internals.
- All 1,556 unit + integration tests pass.

---

## ADR-033: v2 Overlay StringId Resolution in ToSymbolCard

**Date:** 2026-04-03
**Status:** Accepted

**Context:**
`symbols.get_card` in workspace mode crashed with `StorageFormatException:
StringId 42459 out of range [0..42414]` when looking up overlay-local symbols
(symbols added by `index.refresh_overlay` that don't exist in the baseline).

**Root cause:**
`RecordToCoreMappings.ToSymbolCard(sym, reader)` resolved all StringIds through
`EngineBaselineReader.ResolveString()`, which only knows baseline dictionary
entries (IDs 0..42,414). Overlay-local symbols have StringIds in the overlay's
extended range (42,415+), interned in the overlay dictionary during
`ApplyDeltaAsync`. The overlay's `ResolveString` method handles both ranges
(baseline IDs fall through to baseline dictionary, overlay IDs resolve from
overlay dictionary), but it was not being used for card building.

**Fix:**
1. Added `Func<int, string> resolveString` overloads to all four record-to-core
   mapping methods: `ToSymbolCard`, `ToStoredReference`, `ToOutgoingReference`,
   `ToStoredFact`. Original single-arg overloads delegate with `reader.ResolveString`.
2. `CustomEngineOverlayStore` overlay read paths (`GetOverlaySymbolAsync`,
   `GetSymbolByStableIdAsync`, `GetOverlayReferencesAsync`,
   `GetOutgoingOverlayReferencesAsync`, `GetOverlayFactsForSymbolAsync`) now
   pass `overlay.ResolveString` which handles both baseline and overlay string pools.

---

## ADR-034: F# Language Support via FSharp.Compiler.Service

**Date:** 2026-04-03
**Status:** Accepted

**Context:**
Adding F# as the third supported language in CodeMap (after C# and VB.NET).

**Key finding:**
`MSBuildWorkspace.OpenSolutionAsync` cannot load `.fsproj` files — the error is
"file extension '.fsproj' is not associated with a language." No
`Microsoft.CodeAnalysis.FSharp.Workspaces` NuGet package exists.

**Decision:**
Use `FSharp.Compiler.Service` (FCS) directly for F# projects:
- `FSharpChecker.ParseFile` + `CheckFileInProject` for parsing and type-checking
- `PartialAssemblySignature.Entities` for symbol extraction
- `GetAllUsesOfAllSymbolsInFile` for reference extraction
- `entity.XmlDocSig` produces standard doc-comment IDs (`T:`, `M:`, `P:`) —
  compatible with existing `SymbolId` and all storage/query layers

**Architecture:**
- `RoslynCompiler.FindFSharpProjects` scans `.sln` for `.fsproj` entries
  (MSBuildWorkspace never loads them into `solution.Projects`)
- F# projects processed in Pass 0 (before C#/VB Pass 1)
- Bridge classes in `src/CodeMap.Roslyn/FSharp/`:
  `FSharpProjectAnalyzer`, `FSharpSymbolMapper`, `FSharpReferenceMapper`,
  `FSharpTypeRelationMapper`, `FSharpFileExtractor`, `FSharpSyntacticFallback`
- Zero changes to Core, Storage, Query, Mcp, Daemon

**Scope:**
- Symbols, references, type hierarchy — fully supported
- Architectural fact extractors (endpoints, config, DI) — deferred (F# codebases
  rarely use ASP.NET controllers or EF Core DbSets)
- F# scripts (`.fsx`) — not supported

**NuGet dependency:** `FSharp.Compiler.Service` 43.9.100 (~43MB)

**Consequences:**
- F# projects in mixed-language solutions indexed in a single pass
- All 26 MCP tools work for F# symbols
- Package size increases by ~43MB due to FCS dependency

---

## ADR-035: v2 GetFileSpanAsync Out-of-Range Guard

**Date:** 2026-04-03
**Status:** Accepted

**Context:**
`code.get_span` on a file that has been edited since the baseline was built
crashes with `ArgumentOutOfRangeException` in `CustomSymbolStore.GetFileSpanAsync`.
The stored content (from baseline) has fewer lines than the requested range
(which refers to the current working copy).

**Fix:**
Clamp both `start` and `end` against `totalLines` before slicing:
```csharp
var start = Math.Max(0, Math.Min(startLine - 1, totalLines));
var end = Math.Max(start, Math.Min(totalLines, endLine));
```
Returns a truncated span (with `truncated: true`) rather than crashing.

**Consequences:**
- `code.get_span` no longer crashes on modified files
- Returns partial content when baseline is stale (agent sees truncation flag)

---

## ADR-036: FSharpSymbolMapper Namespace Entity Recursion

**Date:** 2026-04-03
**Status:** Accepted

**Context:**
`FSharpSymbolMapper.WalkEntity` returned early when `entity.IsNamespace` was true or
when `entity.XmlDocSig` was empty or threw. This prevented recursion into nested
modules inside namespace declarations (e.g. `namespace Fake.Core` → `module Target =`),
causing those modules and all their symbols to be silently dropped from the index.

**Decision:**
Add explicit early-exit branches for namespace entities and empty/throwing XmlDocSig,
each of which still recurses into `entity.NestedEntities` before returning. No entity
card is created for these entities (they have no usable XmlDocSig), but descendant
modules that do have valid XmlDocSigs are still reached and indexed.

**Consequences:**
- Namespace-scoped F# module hierarchies are now fully traversed.
- Defensive: fixes partial indexing of any repo that uses `namespace Foo` + module pattern.
- 378/378 Roslyn tests pass.

---

## ADR-037: FSharpSymbolMapper [RequireQualifiedAccess] Member Indexing

**Date:** 2026-04-03
**Status:** Accepted

**Context:**
`[<RequireQualifiedAccess>]` on an F# module causes FCS to return an empty string for
`entity.XmlDocSig`. Even after ADR-036, the empty-XmlDocSig branch only recursed into
`entity.NestedEntities` — not `entity.MembersFunctionsAndValues`. So module-level `let`
functions (e.g. `Fake.Core.Target.runOrDefault`) were never indexed: the entity card was
skipped AND the member walk was skipped.

Crucially, even when a module has no XmlDocSig, its individual member functions *do* have
valid independent XmlDocSigs (e.g. `M:Fake.Core.Target.runOrDefault(System.String)`).

**Decision:**
Restructure `WalkEntity` so that `entity.MembersFunctionsAndValues` and
`entity.NestedEntities` are always walked, regardless of whether a valid `XmlDocSig`
was obtained. The XmlDocSig only controls whether an entity-level SymbolCard is emitted.
Entity metadata (`FullName`, `AccessPath`, `DisplayName`) is retrieved before the
XmlDocSig guard so it is available for member card construction even when no entity card
is created.

**Consequences:**
- `[<RequireQualifiedAccess>]` module members are now correctly indexed.
- FAKE's `Fake.Core.Target` module functions (`run`, `runOrDefault`, etc.) appear in index.
- Generalises: any future FCS case where entity XmlDocSig is unavailable still indexes members.
- 379/379 Roslyn tests pass (new test: `ExtractSymbols_FindsMembersOfRequireQualifiedAccessModule`).


---

## ADR-038: `graph.callers` Interface-Aware Resolution

**Date:** 2026-05-11
**Status:** Accepted

**Context:**
Field report (2026-05-11) from an AI agent investigating a config bug:
`graph.callers` on the concrete `SurveyorConfigService.GetCMSIpAddress` returned 2
callers; `refs.find` on the interface `ISurveyorConfigService.GetCMSIpAddress`
returned 7. The 5 real call sites all dispatched through DI on the interface.
Without prior context, the agent would have shipped a fix based on the 2-caller
result and missed the actual handler. This is the failure mode CodeMap exists to
prevent: silently under-reporting in a tool whose value prop is "don't miss things
a grep would miss."

**Decision:**
Resolve interface implementation at **query time** (not at indexing time):

- For methods, properties, events, indexers: derive the containing-type FQN by
  parsing the doc-comment ID (last dot before any `(`). Look up the containing
  type's interfaces via `type_relations`. For each interface, substitute its FQN
  into the doc ID to construct a candidate interface-member SymbolId and probe
  the symbol store.
- Always emit `interface_implementation_hint` on the response when interface
  members are matched (metadata, bounded probe cap = 50 for the count).
- Add opt-in `follow_interface: bool = false`. When true, union concrete callers
  with refs targeting the interface members at the BFS root level, dedupe by
  `from_symbol`.
- Cache key gains `:fi={true|false}` suffix so the two modes don't poison
  each other.

**Why query-time, not storage:**
Member-level `implements` edges in storage would be precise (O(1) lookup, clean
explicit-impl handling) but require a baseline-format bump and forced re-index
for every user. M20-03 is a correctness-only fix; a baseline migration is too
heavy a ship. Promote to stored edges if future field reports surface resolution
accuracy issues.

**Quirk discovered live:**
`card.ContainingType` stores the SIMPLE type name in production extraction
(`"RoslynCompiler"`), not the FQN (`"CodeMap.Roslyn.RoslynCompiler"`). v2.6.0
unit tests masked this because they constructed mock cards with FQN containing
types. v2.6.1 resolver parses the doc ID directly for the FQN and only uses
the card field as a sanity check (must equal or end with `"." + simpleName`).
For explicit-interface impls (`M:Ns.Foo.IFoo.X` with simple type `Foo`),
peel one dotted segment off the parsed container and shift it into the member
tail so the existing explicit-impl prefix-strip logic applies.

**Consequences:**
- Concrete-method `graph.callers` in DI codebases stops under-reporting silently.
- Hint surfaces availability of the union *without* anyone paying for it
  (bounded probe is one indexed lookup per interface member, ≤50 caller scan).
- Backwards-compatible: existing callers see a new optional response field;
  default `follow_interface=false` preserves prior behavior exactly.
- Smoke-tested live on the indexed CodeMap repo: `RoslynCompiler.CompileAndExtractAsync`
  default → 31 callers + hint(6); `follow_interface: true` → 37 callers, hint
  text adapts to "already applied". 1635 unit tests pass.
- Deferred to future milestones: `graph.callees` inverse (interface → impls),
  `virtual`/`override` upward walk, member-level storage edges.

---

## ADR-039: `CODEMAP_HOME` Data-Root Override

**Date:** 2026-09-27
**Status:** Accepted

**Context:**
The multi-agent concurrency benchmark (PHASE-21-01, findings doc §10.2) must start
N real `codemap-mcp` processes. The data root was hard-coded to `~/.codemap` in two
places (`Program.Main` for config + logs, `AddCodeMapServices("~/.codemap")` for
store, overlays and `_savings.json`), so any such run would read and write the
developer's live store.

**Decision:**
Add an optional `CODEMAP_HOME` environment variable, resolved once in
`CodeMapHome.ResolveFromEnvironment()` and passed to both sites.

- Unset/blank → `~/.codemap` (unchanged default).
- `~`, `~/x`, `~\x` → expanded against the user profile.
- Fully qualified → normalised full path.
- Relative → startup fails: one-line message on stderr, exit code 2, stdout untouched.
  MCP clients launch servers from an unpredictable working directory, so resolving
  against CWD would silently scatter stores.
- Scope: `config.json`, `logs/`, `store/` (baselines + overlays), `_savings.json`.
  Not the install layout (`bin/`, `swap.bat`) and not `CODEMAP_CACHE_DIR`.

**Rejected alternatives:**
- Overriding `USERPROFILE`/`HOME` for child processes: unreliable on Windows
  (known-folder API ignores the env var), and it also moves `~/.nuget`, MSBuild
  and git config, which skews the measurement.
- `--home` CLI flag: MCP configs already carry env vars (`CODEMAP_CACHE_DIR`);
  one mechanism is enough.

**Consequences:**
Additive and backward compatible. It's the groundwork for isolated test installs,
CI and per-worktree data roots (ADR-06/ADR-07 in the scaling findings). If ADR-07
later moves the *default* to an OS data dir, `CODEMAP_HOME` remains the override.

---

## ADR-040: Non-Destructive Baseline Publication

**Date:** 2026-09-27
**Status:** Accepted

**Context:**
PHASE-21-01's multi-process driver reproduced F6: with N daemons indexing the same
(repo, commit), publication failed intermittently with `adjacency-in.idx ... used by
another process` or `baselines/<sha> ... already exists`. Root cause
(`EngineBaselineBuilder` phase 13, and identically `EngineBaselineCacheManager`
pull/push): `if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
Directory.Move(temp, final);`. On Windows the delete removed the unmapped files of a
baseline another process had open (`checksums.bin`, `manifest.json`, `search.idx`),
then threw on the first mapped segment, leaving a gutted baseline (verified by
`BaselineConcurrentPublishTests`). `BaselineExistsAsync` checked only the manifest, so a
gutted baseline could be served, and `GetOrOpen` silently skipped a missing search index.

**Decision:**
- Baselines are immutable once published. A single `BaselinePublisher` owns
  publication for the builder and for cache pull/push: an existing complete baseline
  is adopted (staging discarded); an incomplete one is renamed into
  `<repo>/temp/quarantine-*` (never deleted in place); staging → final is an atomic
  rename, and losing that race adopts the winner.
- Completeness = manifest parses + all 12 segment files present
  (`BaselinePublisher.RequiredFiles`). `BaselineExistsAsync` and the shared-cache
  checks use it; `GetOrOpen` throws `StorageCorruptionException` for an incomplete
  baseline instead of serving it.
- An incomplete baseline that cannot be moved aside (still open elsewhere) raises the
  new transient `StorageBusyException`.
- Quarantine dirs are swept best-effort at the start of each build.

**Why adopt instead of replace:** a baseline is a pure function of (repo, commit,
solution, CodeMap version), so a peer's complete baseline is equivalent to ours.
Replacing it buys nothing and can destroy data another process is reading.

**Consequences:** no format change, no re-index (all 331 baselines in a real store
already contain the 12 files). F2 (duplicate builds) is unchanged — only publication
is safe; build coalescing is Phase 2 (shared host). `index.cleanup`/`remove_repo`
still delete on explicit request.

---

## ADR-041: Error Taxonomy for I/O Conflicts and Unexpected Failures

**Date:** 2026-09-27
**Status:** Accepted

**Context:**
Handler outer `catch (Exception)` blocks labelled every failure with one fixed code:
`IndexHandler.Error()` hard-coded `COMPILATION_FAILED` (for storage races, I/O, git
errors *and* validation such as "solution_path not found"); `WorkspaceHandler`,
`OverlayRefreshHandler` and `RepoStatusHandler` returned `INVALID_ARGUMENT`. The
multi-process driver (PHASE-21-01) showed F3 (`overlay.wal` sharing violation) as
`INVALID_ARGUMENT` and F6 (baseline publish race) as `COMPILATION_FAILED`. Agents
reading those codes change their input or hunt for build errors instead of retrying.

**Decision:**
- Three codes: `WORKSPACE_IN_USE` (retryable; `details.workspace_id`),
  `STORAGE_ERROR` (retryable), `INTERNAL_ERROR` (not retryable).
- One classifier, `HandlerHelpers.ClassifyException(ex, operation, workspaceId)`, used by
  every handler outer catch (11 sites): sharing/lock violation → `WORKSPACE_IN_USE`
  (workspace-scoped) or `STORAGE_ERROR`; `IStorageFailure` / `IOException` /
  `UnauthorizedAccessException` → `STORAGE_ERROR`; `ArgumentException` (value-type guards
  such as `WorkspaceId.From` on caller input) → `INVALID_ARGUMENT`; anything else →
  `INTERNAL_ERROR`. Messages keep the original exception text.
- `IndexHandler.Error(string)` now means `INVALID_ARGUMENT`; the genuine
  "no symbols" compile failure keeps `COMPILATION_FAILED` (with `failed_projects`).
- **Dependency rule:** CodeMap.Mcp must not reference Storage.Engine, so the storage layer
  is recognised through a Core marker interface `CodeMap.Core.Interfaces.IStorageFailure`
  (`bool IsTransient`), implemented by `StorageEngineException` (transient = `Busy`).
- Sharing-violation detection: Windows HRESULT `0x80070020` / `0x80070021`; Unix
  `EWOULDBLOCK` (11 Linux / 35 macOS) — the Unix branch is unverified until PHASE-21-03.

**Consequences:** additive codes, envelope unchanged (`retryable` already serialised).
Behaviour change where the old code was wrong (called out in CHANGELOG v2.8.2). Query
tools without an outer catch (e.g. `symbols.search` hitting an incomplete baseline) still
surface as JSON-RPC `-32603` — follow-up.

---

## ADR-042: Per-Checkout Build Lock for MSBuild Evaluation

**Date:** 2026-09-27
**Status:** Accepted

**Context:**
F9 (PHASE-21-03): two CodeMap processes running `ensure_baseline` on the same checkout
raced MSBuild design-time targets on generated files under `obj/`
(`Could not write lines to file "…\obj\Debug\net10.0\EmbeddedAttribute.cs". Cannot create a
file when that file already exists.`). The losing project loaded without the file and
compiled with errors — which still counts as `Compiled` — so a degraded baseline was
published under (repo, commit) and, after ADR-040, adopted by every other process. An
in-process regression test reproduced it in 4/4 runs.

**Decision:**
- `CheckoutBuildLock`: an OS-level `FileShare.None` handle on
  `<TEMP>/codemap-locks/<sha256(checkout root)[..16]>.lock`. Checkout root = nearest ancestor
  with `.git` (directory or worktree file), else the solution directory; normalised and
  case-folded except on Linux. Keyed outside `CODEMAP_HOME` so processes with different data
  roots still exclude each other; the OS releases it if the holder dies.
- Held by `RoslynCompiler.CompileAndExtractAsync` from workspace open through extraction, and by
  `IncrementalCompiler`'s cold-path workspace open. Warm-path document edits do not take it.
- Waiters poll every 250 ms; timeout 30 min (`CODEMAP_BUILD_LOCK_TIMEOUT_SECONDS`), then
  `IOException` → `STORAGE_ERROR` (retryable, ADR-041).
- One retry when an MSBuild `Failure` diagnostic matches a file-conflict pattern
  (conflicts with IDE / `dotnet build`, which the lock cannot see); a second failure is kept.

**Consequences:** same-checkout builds are serialised (not deduplicated — F2 stays for
Phase 2); separate worktrees/clones never contend (0 waits at N=4 isolated, throughput
unchanged). No format change.

## ADR-043: Repo-Scoped Overlays

**Date:** 2026-09-28
**Status:** Accepted

**Context:**
`WorkspaceManager` keys workspaces by `(RepoId, WorkspaceId)`, but the storage layer keyed
overlays by `workspaceId` alone: the `CustomSymbolStore` overlay cache, the directory
`store/overlays/<workspaceId>/`, and `CustomEngineOverlayStore`'s workspace → (repo, commit)
map. The same id in two repos (every agent is told to use `"session"`) therefore shared one
`EngineOverlay` bound to the first repo's baseline, the last `workspace.create` rebound the
map, and delete/reset of one repo's workspace destroyed the other's. Across processes, two
unrelated repos collided on one `overlay.wal` → `WORKSPACE_IN_USE` (F3). The baseline-level
writes in `CustomSymbolStore` (edge upgrades, metadata stubs, virtual files, decompiled-symbol
upgrades) used the commit SHA as the overlay key and collided the same way for two RepoIds at
one commit (e.g. two remote-less clones).

**Decision:**
- Overlays live at `<store>/<repoId>/overlays/<key>/` next to that repo's `baselines/`;
  the in-memory cache and `_wsToCommit` are keyed by `(repoId, key)`. Key = workspace id, or
  the commit SHA for the baseline-level writes. Internal methods only — `IOverlayStore` already
  carried `RepoId` on every call, so no interface change (`ISymbolStore` stays at 27).
- The legacy flat `store/overlays/` tree is never read or written. Overlays are session scratch
  state, so there is no migration: a workspace re-created after the upgrade starts empty.
  `index.cleanup` (non-dry-run) sweeps the legacy tree best-effort, skipping any directory
  whose `overlay.wal` cannot be opened `FileShare.None` (held by a running pre-v2.8.2 daemon —
  .NET enforces this on Unix too), and removes the root once empty.
- `index.remove_repo` deletes `<store>/<repoId>/`, which now includes that repo's overlays.

**Consequences:** the same workspace id in two repos is fully isolated in one process and
across processes; F3 remains only for the same `(repo, workspace)`. No baseline change, no
re-index. A workspace id that equals a commit SHA still shares the baseline-level overlay of
that repo (pre-existing, unchanged).

## ADR-044: Exclusive Overlay Writer Lock (F4)

**Date:** 2026-09-28
**Status:** Accepted

**Context:**
F4 (PHASE-21-04): `WalWriter` opens `overlay.wal` with `FileShare.Read`. On Unix .NET emulates
`FileShare` with advisory `flock`: `FileShare.None` takes `LOCK_EX`, every other mode `LOCK_SH`.
A second process opening the same overlay therefore succeeded without error, replayed the WAL,
wrote the manifest if absent, and appended its own records. On dispose, each process checkpoints
its in-memory state and truncates the WAL, so **whichever process checkpointed last silently
discarded the other's edits.** Reproduced in the Linux rig: after two writers,
the owning writer's `T:MyApp.Yod` was gone. Windows was already protected by F3. The per-overlay
`lock` file in `STORAGE-FORMAT.MD` had been documented but never implemented.

**Decision:**
- `EngineOverlay` opens `<overlay>/overlay.lock` with `FileShare.None` right after creating the
  directory, **before** reading or writing manifest, snapshot or WAL. It holds the lock for its
  lifetime and releases it last in `Dispose` (after the graceful checkpoint), or on constructor
  failure. The file is never deleted while held; it goes with the overlay directory.
- The lock is a live handle, not file presence. The OS releases it when the process dies, so a
  crashed holder never blocks the next opener.
- Failure surfaces the OS `IOException` unchanged: `WORKSPACE_IN_USE` for workspaces,
  `STORAGE_ERROR` for commit-keyed overlays (ADR-041). No waiting inside the store.
- The same code path runs on every OS (on Windows it's redundant with the WAL share mode, so the
  Windows suite covers it).

**Also fixed (found by the same Linux rig, all OSes):** `BaselinePublisher.Publish` (ADR-040)
could quarantine a *complete*, freshly published baseline. A peer's atomic rename landed between
the "is it complete?" check and the "does it exist?" check. It now re-checks completeness before
quarantining. Since publication is one atomic rename, an existing final directory is either
complete or genuinely gutted.

**Consequences:** at most one process writes an overlay on every OS; F3 (`WORKSPACE_IN_USE`) is
now the only cross-process behaviour for the same workspace. Not enforced where `flock` is a
no-op (some network filesystems / container bind mounts); the CodeMap data root belongs on local
storage. No format change beyond the new lock file, no re-index.

## ADR-045: Classify Escaped Tool Exceptions at the `tools/call` Boundary

**Date:** 2026-09-28
**Status:** Accepted

**Context:**
ADR-041 gave the write tools honest error codes via `HandlerHelpers.ClassifyException` in each
handler's catch. The 20 read-only tools have no such catch, and `McpServer.HandleToolCallAsync` had
none either, so any exception escaping a tool reached `DispatchAsync`'s catch-all and became JSON-RPC
`-32603 Internal error`: no CodeMap code, no `retryable`, and shown by most clients as a protocol
failure. Reproduced for sharing violations, storage failures (including the "run
index.ensure_baseline" message), argument exceptions and plain bugs (PHASE-21-04 T02, 5/5).

**Decision:**
- `HandleToolCallAsync` wraps `tool.Handler(...)`. `OperationCanceledException` propagates; any other
  exception is logged at Error and returned as the standard error envelope via the new
  `HandlerHelpers.ClassifyUnhandled`.
- `ClassifyUnhandled` = `ClassifyException` except **`ArgumentException` → `INTERNAL_ERROR`**. Inside a
  handler's try, argument exceptions come from value-type guards on caller input (ADR-041 keeps
  `INVALID_ARGUMENT` there). One that escapes the handler was not caught by input validation, so it
  is a defect (v2.6.2: `SymbolId.From("")` on an overlay symbol).
- `INTERNAL_ERROR` from the boundary carries `details.exception_type`, the diagnosability that
  `-32603`'s `error.data.exceptionType` provided since v2.6.2.
- The boundary uses only an explicit `workspace_id` argument (no sticky-default lookup: the boundary
  does no I/O). A sharing violation without one is `STORAGE_ERROR` (retryable).
- `-32603` stays for failures outside any tool.

**Consequences:** clients always get a machine-readable code and `retryable` for tool failures.
Bugs are still loud (Error log, `isError: true`, `INTERNAL_ERROR` with the exception type), but they
are no longer protocol errors. Finding: the realistic query-side storage failure (gutted baseline)
never threw; the query path pre-checks completeness and already returned `INDEX_NOT_AVAILABLE`, so
this boundary is defence-in-depth for unexpected exceptions.

## ADR-046: Report Source Generators the Compiler Refused to Load (F10)

**Date:** 2026-09-28
**Status:** Accepted

**Context:**
F10 (PHASE-21-05): on .NET SDK 10.0.401 the Razor source generator references Roslyn 5.9; CodeMap
bundles 5.3. Roslyn refuses to load a generator built for a newer compiler
(`ReferencesNewerCompiler`), and MSBuildWorkspace swallows that. The compilation succeeds without
the generated code, so Razor components, `@code` members and `@page` routes vanished from the index
while the run reported `semantic_level: full`, `confidence: high` (the Blazor sample: 6 symbols).
It isn't OS-specific: any machine whose SDK is newer than CodeMap's Roslyn is affected, including
users of the shipped v2.8.1. Bumping Roslyn fixes current SDKs, but the next SDK band can break it
again, so the silence is the defect to fix first.

**Decision:**
- `GeneratorLoadFailureTracker.Inspect(project)`: for every `AnalyzerFileReference` that yields no
  generators, read its metadata (no load). If it declares a `[Microsoft.CodeAnalysis.Generator]` type,
  a generator failed to load. The reason comes from the `Microsoft.CodeAnalysis` version it references:
  newer than the running compiler → `ReferencesNewerCompiler (built for Roslyn X, CodeMap runs Y)`,
  else `UnableToLoadGenerator`. Plain analyzers are ignored (they don't affect the index).
- **Not the `AnalyzerLoadFailed` event**: it fires once per analyzer reference, which is shared and
  cached across projects, so a second project using the same generator would stay silent.
- Results go to `ProjectDiagnostic.GeneratorLoadFailures` (additive, null when none). Such a project
  doesn't count as fully compiled in `ComputeSemanticLevel` (→ at most `Partial`), confidence drops to
  `Medium`, and a Warning naming the fix is logged once per failure per run.
- `Compiled` stays true (the compilation did succeed); the level and the new field carry the degradation.

**Consequences:** a generator/compiler version mismatch is visible in `index.ensure_baseline`, in
every query's `semantic_level` and in the log, instead of silently returning an incomplete index at
full confidence. The Roslyn bump itself is PHASE-21-05 T02. Baselines built silently degraded before
the upgrade aren't detected retroactively (KNOWN-LIMITATIONS L-11).

## ADR-047: `CODEMAP_CACHE_DIR` Follows the `CODEMAP_HOME` Path Rules (F11)

**Date:** 2026-09-28
**Status:** Accepted

**Context:**
F11 (found during PHASE-21-05 T02): `ServiceRegistration` passed `CODEMAP_CACHE_DIR` to
`EngineBaselineCacheManager` as-is. A blank value (e.g. an MCP client config with
`"CODEMAP_CACHE_DIR": ""`, or a script's `CODEMAP_CACHE_DIR=`) counted as an *enabled* cache, and
`Path.Combine("", repoId, sha)` is relative. So every new baseline was pushed into the daemon's
working directory, which MCP clients usually set to the repo root, leaving an untracked
`local-<hash>/<sha>/` in the user's working tree that can get committed. A relative value had the same
effect, anchored wherever the client happened to start the server. Reproduced by a subprocess test:
`…\cwd\local-b73d0e677cfb93f6` appeared in the working directory after `ensure_baseline`.

**Decision:**
- `CodeMapHome.ResolveCacheDir(env, profile)` (pure, next to ADR-039's `Resolve`): unset/blank →
  `null` (disabled); `~`, `~/x`, `~\x` → expanded; fully qualified → normalised; relative →
  `INVALID_ARGUMENT`. `Program` resolves it at startup: stderr message + exit code 2 on failure, like
  `CODEMAP_HOME`.
- `AddCodeMapServices(baseDir, sharedCacheDir)` receives the resolved value and no longer reads the
  environment variable.
- Defence in depth: `EngineBaselineCacheManager` treats a blank directory as disabled.

**Consequences:** a blank value now means "no cache"; a relative value fails loudly instead of
writing into an arbitrary directory. Anyone relying on a relative cache dir (which only ever worked
relative to the client's working directory) must switch to an absolute path. Not addressed:
`config.json`'s `shared_cache_dir` is still not read (documented gap).

---

> **ADR-048…053 are the multi-agent scaling ADRs 01–06** from
> `docs/CODEMAP-MULTI-AGENT-SCALING-FINDINGS.md` §8, renumbered because this log already uses
> ADR-001…047. Drafted as Proposed in PHASE-21-06 T02; **all six accepted by the user on 2026-09-29**,
> ADR-051 and ADR-053 with the refinements noted in them. Evidence is from Phase 0 (PHASE-21-01…06)
> unless marked otherwise.

## ADR-048: Process Model — Shared Host + Thin Adapter, Standalone Retained (Scaling ADR-01)

**Date:** 2026-09-28
**Status:** Accepted (2026-09-29, by the user)

**Context:**
Today every MCP client (and every sub-agent) starts its own stdio daemon (topology T1). Phase 0 measured
T1 on five repos at N = 1, 4, 8, 16 agents (`docs/benchmarks/phase-21-06/`, findings §6.5):
- **Memory (F1) is linear in N with no sharing.** Peak total working set at N=16 is 13.9–16.2 × N=1;
  per-process memory is flat per repo: ~0.3 GB (SampleSolution), ~0.5 GB (eShopOnWeb, CodeMap),
  ~2.5 GB (Bitwarden), ~3.9 GB (nopCommerce). nopCommerce at N=8 used 25.8 GiB; N=16 was projected
  at 62.4 GiB on a 64 GB machine and not run.
- **Every agent builds the same baseline (F2), and the duplicates slow each other down.** Cold build
  p50 on Bitwarden: 79 s alone, 129 s at N=8, 178 s at N=16 (16 processes, 35.5 GiB, for work one
  build does in 79 s). With a 120 s client timeout, no agent at N=8 or N=16 ever got an index.
- **Per-agent throughput falls to 44–51% of N=1 at N=16** on the small/medium repos. Light-read
  latency stays low (`symbols.search` p95 ≈ 10–11 ms at N=16), so the host is not latency-bound
  there. The cost is memory and build time.
- Process start is cheap (~250 ms); the cold build dominates time to first answer (F8).

**Options:** (1) keep stdio-only T1; (2) opt-in HTTP only (clients that support it share a host;
stdio clients stay T1); (3) shared host per user/data root + thin stdio adapter, standalone retained.

**Decision:** **(3)**. One host owns Roslyn/MSBuild, baseline builds (coalesced per
repo + commit) and all overlay writers. Stdio clients reach it through a thin adapter (no Roslyn;
ADR-049 decides the IPC). HTTP-capable clients connect directly (Phase 3). **Standalone T1 stays**
for single-agent users, CI and diagnostics, and remains the default until the Phase 6 gates pass
(findings ADR-12).

**Consequences:** Phases 2–4 of findings §9. Gates are measured with this harness against these T1
numbers. Findings §10.2 asks for ≤ 1.5× memory at N=8. T1 is 7.1–8.0× at N=8 on every repo that ran, so
the gate is far from today's numbers and meaningful. Build coalescing alone (the F2 fix inside a host)
removes the N-fold cold-build time the numbers above show.

**Design input for Phase 2 (added at acceptance):** `IncrementalCompiler` (singleton,
`src/CodeMap.Roslyn/IncrementalCompiler.cs`) caches **one** Roslyn `Solution`, keyed by the solution's
absolute path, and stores each refresh's edited documents back into that cache. That fits T1 (one
process, one checkout). In a shared host serving agents in different worktrees it would (a) evict the
cache whenever a refresh comes from another worktree, so every refresh takes the cold path (~1.4 s on
CodeMap, far more on large repos, instead of ~63 ms), and (b) if shared, carry one agent's edits into
another agent's compilation. The host needs one base `Solution` per (repo identity, commit, solution)
plus an immutable fork per workspace, with documents matched by **repo-relative** path, not absolute
path (the ADR-053 identity principle applied to Roslyn). Forks share syntax trees, metadata references
and unaffected project compilations, so per-agent cost is the changed documents plus the recompiled
projects. Overlay change detection already diffs against the baseline commit
(`WorkspaceManager.RefreshOverlayAsync`), so an agent committing on its own branch needs no new
baseline.

## ADR-049: Adapter ↔ Host IPC (Scaling ADR-02)

**Date:** 2026-09-28
**Status:** Accepted (2026-09-29, by the user)

**Context:**
If ADR-048 adopts a shared host, stdio-only clients need a thin adapter that relays JSON-RPC to it.
The candidates are loopback HTTP (MGsCodeMapMCP) and named pipes / Unix domain sockets (charpcodemap).
Phase 0 measured nothing that separates them. Every Phase 0 number is topology T1 (one process per
agent, no IPC hop). The harness already hides transport behind `IMcpClient`, so a T2/T3 client can be
measured with the same workload later.

**Options:**
1. Loopback HTTP: one stack shared with direct-HTTP clients (Phase 3). Port allocation and conflicts;
   needs `Origin`/`Host` validation and a token against DNS rebinding and other local users.
2. Named pipes (Windows) + Unix sockets: no ports; the OS ACL / `0600` restricts access to the user;
   no browser-reachable surface. It's a second transport to maintain beside HTTP.
3. Both.

**Decision:** **defer the choice to a Phase 4 spike, with these acceptance criteria fixed
now**: works without admin rights on Windows, Linux and macOS; adds ≤ 1 ms p95 per call over T1 at
N=16 on the Phase 0 workload; no listener reachable by other users or by a browser. Until then, host
internals stay transport-agnostic, and the host's session layer must not assume HTTP. Leaning: pipes/
sockets for the adapter (option 2), because they remove the port and DNS-rebinding class of problems
by construction.

**Consequences:** Phase 2 and 3 can proceed without this decision. The spike reuses the harness
(`IMcpClient`) so the criteria are measured, not argued.

## ADR-050: MCP Implementation — Custom JSON-RPC vs the Official C# SDK (Scaling ADR-03)

**Date:** 2026-09-28
**Status:** Accepted (2026-09-29, by the user)

**Context:**
CodeMap's MCP layer is hand-rolled stdio JSON-RPC (protocol `2025-03-26`, tool annotations since
v2.8.0). Phase 0 hardened its error boundary (ADR-041, ADR-045), and nothing measured in Phase 0 points at
it as a bottleneck. At micro size one daemon served 50.1 calls/s for one agent (PHASE-21-06 T02, SampleSolution N=1).
Streamable HTTP, sessions and newer protocol revisions (Phase 3) are exactly what the official SDK
provides, and would be real work to hand-roll.

**Decision:** **keep the custom stdio implementation now; spike the official SDK at the
start of Phase 3 and adopt it if**: (a) the full tool surface, annotations, usage hints and error
envelopes pass the existing Mcp/Integration suites unchanged; (b) p95 per call within 10% of the custom
stack on the Phase 0 workload; (c) it supports the protocol revision Phase 3 targets. If the spike
fails, Phase 3 implements Streamable HTTP on the custom stack.

**Consequences:** no churn before it pays off; the decision is made on measured conformance and latency.

## ADR-051: Tool Naming (Scaling ADR-04)

**Date:** 2026-09-28
**Status:** Accepted (2026-09-29, by the user)

**Context:**
Tool names are dotted (`symbols.search`). The findings doc plans MCP-safe names (§9 Phase 1, mapping
in §12); the Phase 1 exit criterion is `^[a-zA-Z0-9_-]{1,64}$`, which dotted names fail.
**Correction at acceptance [verified 2026-09-29]:** the MCP spec itself does *not* forbid dots. Revision
2025-11-25 ("Tool Names") says names SHOULD be 1–128 characters of `A-Z a-z 0-9 _ - .` and gives
`admin.tools.list` as a valid example. The constraint comes from the LLM APIs the client forwards tools
to: the Claude API requires `^[a-zA-Z0-9_-]{1,128}$` for a tool `name` (no dot), so every client has to
rewrite dotted names, and each does it its own way. The 64-character cap in the exit criterion is kept as
a conservative bound for other providers. [verified in this environment] Claude Code already exposes the
tools as `mcp__codemap__symbols_search`, i.e. the client maps `.` → `_` and adds its own server prefix. A
`codemap_` prefix inside the name would therefore be doubled in such clients.

**Decision:** canonical names become underscore names (`symbols_search`, mapping in findings
§12). Dotted names stay accepted as **aliases for at least two minor releases**, answered normally
plus a deprecation note in the response. **No prefix by default**, because clients already namespace by
server; an optional configurable prefix only if a client that doesn't namespace is reported.
Documentation (`codemap.guide`, CLAUDE-INSERT, README, API-SCHEMA, hint texts) switches to the new
names in the same release.

**Consequences:** Phase 1 work (rename checklist in findings §12). Two names per tool during the alias
window; the tool-count tests count canonical tools only.

## ADR-052: Workspace Sharing Semantics (Scaling ADR-05)

**Date:** 2026-09-28
**Status:** Accepted (2026-09-29, by the user)

**Context:**
Today, two processes can't share a workspace at all: the second gets `WORKSPACE_IN_USE` (F3, ADR-041;
the overlay writer lock, ADR-044, makes that true on every OS). So F5 (stale overlay views) and F7
(L1 cache incoherence) can't occur yet. In a shared host they become possible, and the ID decides who
shares. The shipped guidance (CLAUDE.md, `codemap.guide`) tells *every* agent to call
`workspace.create { workspace_id: "session" }`. In a shared host, "same ID = shared" would silently
make every agent in a repo share one overlay: agent A's unsaved edits in B's answers.

**Options:** (1) same ID = shared, as today's leaning; (2) IDs are scoped per client session unless
the client opts into sharing; (3) no sharing.

**Decision:** **(2): workspace IDs are session-scoped by default; sharing is an explicit
opt-in** (e.g. `workspace.create { workspace_id, shared: true }` or a `shared:` prefix, settled in
Phase 2). A shared workspace has one writer lane (findings §7.3); readers pin a revision per query.
Sticky defaults (the v2.4.0 auto-defaults) are session-scoped. The CLAUDE.md "session" convention
keeps working because each session's "session" is its own.

**Consequences:** changes the leaning in findings §8 (which assumed same-ID sharing) because of the
"session" convention. F5/F7 tests move into Phase 2's exit criteria (0 cross-workspace leakage).
Standalone (T1) behaviour is unchanged.

## ADR-053: Storage Identity for Baselines and Overlays (Scaling ADR-06)

**Date:** 2026-09-28
**Status:** Accepted (2026-09-29, by the user)

**Context:**
Baselines are keyed by `(RepoId, commit)`, where `RepoId` is a hash of the remote URL, or
`local-<pathHash>` without a remote [verified]. So two worktrees or clones of a remote-less repo get
different RepoIds and each re-indexes the same commit. That's why the harness uses a local clone with
an `origin`. Overlays are keyed by `(RepoId, workspace)` since ADR-043. Phase 0 also showed the index for
"the same commit" is **not** a pure function of the commit: it depends on restore state (F12: an
unrestored checkout indexed eShopOnWeb with 15,158 compile errors, 39% of references missing) and the
SDK's generators (F10).

**Options:** A. key by repo root path; B. repo identity + commit for baselines, overlays per
worktree; C. content-addressed (commit + solution path + build inputs).

**Decision:** **B, with two refinements**:
1. Repo identity = remote URL hash when there is a remote, else the **root commit hash** (stable
   across clones and worktrees of the same history) instead of the path hash. Worktrees of one repo then
   share baselines; forks share only commits that are identical anyway. **Refinements at acceptance:**
   if the history has several root commits, the **oldest** one is used, ties broken by the lower hash (deterministic across clones);
   a **shallow clone** (whose visible root is a graft, not the real root) keeps the path-hash identity
   as today rather than get a wrong one, and so shares nothing until it is unshallowed.
2. A baseline is shareable only if it was built **complete**: every project compiled from restored
   inputs, no generator load failures (i.e. semantic level Full once F12 is reported). Degraded baselines
   are kept per checkout and never pushed to the shared cache. The solution path is part of the baseline
   key (two solutions in one repo, GH #5).
Overlays stay per `(repo identity, worktree, workspace)`.

**Consequences:** Phase 5 scope; needs the F12 reporting first so "complete" is knowable. Migration:
existing `local-<pathHash>` baselines keep working and are re-keyed or rebuilt on the next commit. The
baseline copy-between-roots test (PHASE-21-06 T02) is the precondition that a baseline is
location-independent.

## ADR-054: Persist Project Diagnostics; One Semantic-Level Rule (F13), Report Unrestored Projects (F12)

**Date:** 2026-09-29
**Status:** Accepted (spec approved by the user, PHASE-21-07; T01 F13, T02 F12)

**Context:**
F13 (found while scoping F12, PHASE-21-07 §1.2, reproduced by three tests failing on `bf103d9`):
`ManifestWriter` persisted only `ProjectName`, `Compiled`, `SymbolCount` and `ReferenceCount` of each
`ProjectDiagnostic`, and `CustomSymbolStore.GetSemanticLevelAsync` recomputed the level from `Compiled`
alone, with its own copy of the rule. Every query's `meta.semantic_level` and `codemap.summarize` come from the
store. So a degradation the build detected, namely F10's generator failures (ADR-046), appeared only in the
`index.ensure_baseline` response, and every query against that baseline said `full`. **ADR-046's
consequence "visible … in every query's `semantic_level`" did not hold until this ADR.** Any F12
detection would have been lost the same way.

**Decision (F13, T01):**
- One rule, in Core: `SemanticLevels.IsComplete(ProjectDiagnostic)` / `SemanticLevels.Compute(…)`.
  `RoslynCompiler.ComputeSemanticLevel` and `CustomSymbolStore.GetSemanticLevelAsync` both delegate to it.
- The manifest keeps `errors`, `target_frameworks` and `generator_load_failures` per project as
  additive optional JSON properties, omitted when null. **No format version bump:** System.Text.Json ignores
  unknown properties, so older binaries read newer manifests, and newer binaries read old manifests as "none
  recorded", which is exactly today's behaviour.
- Not retroactive: baselines written before this change keep reporting what they reported.

**Context (F12, T02):** MSBuildWorkspace doesn't restore, and it reports nothing when `project.assets.json`
is missing. A probe showed no workspace diagnostic at all (NETSDK1004 is not raised in design-time
builds). An unrestored project then gets **no metadata references**: SampleSolution's net9.0 projects
had 0 references, including the framework (`CS0400: 'System' could not be found`), while a net10.0
project without packages compiled cleanly with 167 references (its framework ships with the SDK).

**Decision (F12, T02):**
- `RestoreOutputProbe` (no MSBuild evaluation): a project *uses restore* if it's SDK-style or has a
  `PackageReference`. Its restore output is *found* if `project.assets.json` is in `<projectDir>/obj/` or
  within 4 levels up from the intermediate assembly (`CompilationOutputInfo.AssemblyPath`), never above
  the solution dir. That covers `BaseIntermediateOutputPath` overrides and the artifacts layout.
  Otherwise, `ProjectDiagnostic.MissingRestoreOutput` = the expected solution-relative path.
- `SemanticLevels.IsEnvironmentDegraded`: generator load failures, **or** missing restore output
  **together with** compile errors. A clean compile without restore output is reported, not downgraded.
  Environment-degraded projects aren't complete (→ `partial`).
- A Warning per affected project per run names the fix (`dotnet restore`, then rebuild the baseline).
- `index.ensure_baseline` doesn't push an environment-degraded baseline to the shared cache (ADR-053
  point 2). A baseline with genuine compile errors is inherent to the commit and is pushed as before.
- Not done: restoring automatically (runs MSBuild/NuGet against the user's checkout), and rebuilding an
  existing degraded baseline after a restore (needs replace semantics compatible with memory-mapped
  readers; ADR-040). Both are follow-ups.

**Consequences:** the level a query reports is the level the build computed. Any future per-project
degradation signal only needs a field on `ProjectDiagnostic`, the DTO property and a clause in
`SemanticLevels.IsComplete`; the rule can no longer diverge between build and read.

## ADR-055: `symbols.search` Query Syntax on the v2 Engine (PHASE-21-08)

**Date:** 2026-09-29
**Status:** Accepted (spec approved by the user, PHASE-21-08; T01 syntax, T02 multi-kind filter)

**Context:**
`symbols.search` documented SQLite FTS5 syntax (`Foo OR Bar`, `"phrase"`), and the agent guide recommended
`OR` as the fix for an empty multi-term search. The v2 engine (the only engine since v2.1.0) never
implemented it: `SearchIndexReader.NormalizeQuery` lowercased and split the query and **intersected**
every token, `OR` included, and kept `"` inside tokens. So every `OR` query and every quoted query
returned 0 hits. The overlay path (`CustomEngineOverlayStore.SearchOverlaySymbolsAsync`) had its own
tokenizer (space and `.` only, `*` and quotes kept), so workspace-mode results differed from committed mode.

**Decision:**
- One parser, `SearchQuery.Parse` (Storage.Engine), for baseline and overlay: OR of AND-groups. A
  standalone uppercase `OR` separates groups (lowest precedence, as FTS5); a standalone `AND` is
  ignored; `"`, `(`, `)` are ignored; a trailing `*` per word is ignored (prefix matching is native).
  Each word is tokenized exactly as before (lowercase, separator split), so single-term results are unchanged.
- Baseline: postings intersection per group, union across groups; a symbol's score is the best score of
  any group it matches (each group scored with its own text), so `Foo OR Bar` ranks both exact names first.
- `NOT` / `NEAR` (uppercase, standalone) → `INVALID_ARGUMENT` naming the operator; a query with only
  operators/punctuation → `INVALID_ARGUMENT` (Query layer, `FtsQuerySanitizer`).
- Quoted words mean "all of these words", not adjacency: the index stores token sets per symbol, not
  positions. Parentheses don't nest. Both documented.
- Descriptions and hints describe this syntax, not FTS5. The empty-result hint suggests `A OR B` for a
  multi-word query instead of a useless `query*`.

**Consequences:** the documented recovery path (`OR`) works. Workspace and committed mode match the
same symbols for the same query. Query-time only: no format change, no re-index. A trailing `*` no longer
suppresses the exact/prefix-name bonus in scoring (the old scorer compared against the raw text, `*` included).

**Decision (T02, multi-kind filter):** `SymbolSearchFilter` gains `Kinds` (2+ engine kind codes), checked
in `SearchIndexReader.PassesFilter` next to `Kind`, before scoring and the limit.
`CustomSymbolStore.SearchSymbolsAsync` passes every requested kind into the engine and no longer
post-filters. Before, 2+ kinds were filtered after the engine had cut the result to `limit` across all
kinds, so results were lost and `truncated` was wrong (the GH #6 family: filter before limit). The overlay
path already filtered kinds before its limit; browse-by-kinds was already correct.
