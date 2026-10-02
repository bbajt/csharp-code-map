# Changelog

All notable changes to CodeMap are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed
- **A reverted or deleted file no longer stays in the workspace's compilation** (PHASE-21-25). A refresh compiled
  against the last text it had seen of every file, so after an edit was reverted (or a file deleted), a later refresh
  of another file still bound calls to the reverted method or the deleted type, and `refs_find` reported those calls
  as resolved. Reverted and deleted files now reach the compiler.
- **A new file named in `file_paths` is compiled** (PHASE-21-25). After the first refresh, a file created since was
  skipped; its symbols appeared only once the solution was opened again.
- **Cancelling a request now cancels it** (PHASE-21-24). A client's `notifications/cancelled` was ignored: the request
  ran to the end and was answered. Now a running request is stopped, one still waiting is dropped, and neither is
  answered (as the MCP spec asks). A workspace refresh can be cancelled while it compiles; once it writes it finishes,
  so the workspace is never left between two revisions. A shared `index_ensure_baseline` build is not stopped by one
  caller's cancel.
- **A refresh no longer fails while an editor saves the file** (PHASE-21-24). The refresh read source files without
  sharing write access, so a save in progress failed it (`WORKSPACE_IN_USE`), or, on a first refresh, silently left
  the file out of the workspace's text. It now reads like an editor does; a read that overlaps a save is corrected by
  the next refresh.
- **A stray cancellation inside a tool no longer drops the connection** (PHASE-21-24). An `OperationCanceledException`
  the client did not ask for (a library's own timeout) ended the server loop; it is now an `INTERNAL_ERROR` result.
- **`count++`, `--count` and fields or properties passed as `out` / `ref` were recorded as reads.** They are
  now `Write` references, like `count = …` and `count += …`, so `refs_find { kind: Write }` finds them
  (L-16, PHASE-21-14). C# only (VB has no `++`, and its `+=` was already a write). Visible after the next
  baseline build; the total number of references doesn't change.
- **`code_search_text` searched build output**, although its description says "no bin/obj". Generated files
  under `obj/` (Razor `*_razor.g.cs`, `GlobalUsings.g.cs`, …) filled results with `#pragma checksum` and
  `#line` lines. They are now skipped, and `total_files` counts only the files searched. To search them on
  purpose, pass a `file_path` inside `obj/`. Works on existing indexes; no rebuild (PHASE-21-14).
- **Workspace edits were stored without their file, and later edits never cleaned up** (L-19, L-25, L-30,
  PHASE-21-16 T01). A symbol edited in a workspace now keeps its file, containing type, project and stable id, so
  cards and search hits show the real path and its definition span opens. Refreshing a file replaces what earlier
  refreshes stored for it, so a method removed by a later edit disappears and an undone deletion comes back. A
  method added by an edit is reported as a caller instead of an empty id. No re-index; an existing workspace keeps
  its pre-fix records until `workspace_reset`. The workspace log gains record type 0x0B, which 2.10.0 and older
  ignore (after a downgrade, run `workspace_reset`).
- **Workspace surfaces, hierarchy, level and reference resolution ignored the workspace** (L-17, L-18, L-20,
  L-21, PHASE-21-16 T02). `surfaces_list_endpoints`, `list_config_keys` and `list_db_tables` list what a workspace
  edit added, and drop routes and config reads it removed. `types_hierarchy` shows base types and interfaces as
  edited: the incremental compiler now extracts them for the changed files, and a type that exists only in the
  workspace no longer answers NOT_FOUND. A workspace answers with the level
  of its last refresh when that is worse than the baseline's, kept in `overlay.level.json` next to the workspace
  log. Unresolved workspace references are resolved, and the resolved edge replaces the unresolved one. Still
  open: a table removed only in the workspace stays listed there. No re-index.
- **An undone or deleted file kept its workspace edits** (L-31, L-32, PHASE-21-17). `index_refresh_overlay`
  without `file_paths` now also revisits the workspace's files that git no longer reports: an edit that was
  reverted, or a new file that was deleted, disappears from the workspace and the committed answers return. A file
  deleted from the working copy is hidden in the workspace (routes, config keys, references and search hits) until
  it is restored. No re-index. A deletion recorded by 2.10.0 stays hidden after a restore until `workspace_reset`.
- **Cards lost the extractor's stable id, confidence, documentation and signature** (L-24, L-27, L-28,
  PHASE-21-18). A rename now keeps its stable id, so `index_diff` reports "Renamed". A symbol from a project that
  did not compile says `low` confidence. Cards show the `<summary>` (references such as `<see cref>` now keep their
  names) and the real signature, and search hits carry the signature and a documentation snippet. Baseline format 2.1 (additive; 2.10.0 still reads it). **Existing baselines
  keep the old behaviour until rebuilt:** new commits get the fix; for the current commit run `index_remove_repo`,
  then `index_ensure_baseline`.
- **DLL navigation (M12) answered nothing, and failed in a second daemon** (L-23, L-29, L-33, PHASE-21-19). A
  framework or package type resolved on demand now has a card, a search hit and its base types and interfaces;
  `symbols_get_card` shows its decompiled source (`source: "decompiled"`) and `code_get_span` serves the virtual
  file. The data lives in each daemon's memory (at most 256 decompiled files per commit), so several agents on one
  store no longer fail with `overlay.lock`. `index_cleanup` removes the `overlays/<sha>/` directories older versions
  wrote and reports them as `baseline_level_overlays_removed`. No re-index.
- **DLL types were NOT_FOUND until the daemon had compiled a workspace edit** (L-34, PHASE-21-20 T01). A DLL lookup
  now opens the baseline's solution itself when the daemon has none loaded, so it works right after
  `index_ensure_baseline` (also when the baseline already existed or came from the shared cache). A new baseline
  records which solution it was built from (manifest `solution_path`, relative to the repo root); an older one uses
  the solution found at the repo root, which can be the wrong one when the indexed solution is nested. It searches every
  project, so a package referenced only by a later project (e.g. a test framework) resolves too, and it never
  answers from another repo's solution. The first lookup in a daemon pays one solution open (about as long as a
  workspace's first refresh); the solution then stays loaded while used and is freed after 10 idle minutes. If
  another CodeMap process is building the same checkout, the lookup gives up after 10 s (NOT_FOUND) instead of
  waiting. No re-index.
- **F# types were indexed several times and had no hierarchy** (L-26, PHASE-21-21). A type declared in file k of
  an F# project's n files appeared n−k+1 times in search, tagged with the wrong files, and `types_hierarchy` /
  `refs_find` returned nothing for it. Each type is now extracted once, in its declaring file, with its relations.
  The baseline builder also attaches the edges of any repeated symbol id to the copy queries return (C# too, e.g.
  `T:Program` in two projects: callers, references and hierarchy of such ids can only gain results). **Rebuild F#
  baselines** to get the fix (`index_remove_repo` + `index_ensure_baseline`, or the next commit).
- **Workspace spans, card source and text search showed the committed text** (L-22, PHASE-21-20 T02). In a
  workspace, `code_get_span`, `symbols_get_definition_span`, the `source` of `symbols_get_card`,
  `symbols_get_context`, the excerpts of references added by an edit and `code_search_text` now read the text the
  file had at the last `index_refresh_overlay`, so the lines match the symbol's workspace line numbers (they used to
  be the working copy's line numbers over the committed text). A file deleted in the workspace answers NOT_FOUND
  for spans and is left out of text search. Committed mode is unchanged. The workspace keeps one copy of each edited
  file's text. No re-index; a workspace refreshed by an older version shows committed text for a file until that
  file is refreshed again.

### Internal
- **Groundwork for concurrent requests in one server (M21 Phase 2, PHASE-21-22).** No visible change today: the server
  still handles one request at a time.
  - Create, refresh, reset and delete of one workspace run one at a time.
  - Workspace answers are cached per refresh generation instead of per revision, which a reset reuses.
  - A read holds the index files or workspace it uses until it ends; removing a repo, cleaning up, rebuilding a
    commit or deleting a workspace no longer closes them under it. A baseline still being read is reported in
    `skipped_in_use`, and a workspace reset or re-create waits for the reads of the old one (at most 30 s, then a
    retryable `STORAGE_ERROR`).
  - Concurrent `index_ensure_baseline` calls for one commit share one build and get its answer.
- **MCP sessions in one process (M21 Phase 2, PHASE-21-23).** Not used by the shipped daemon, which stays one stdio
  client with unchanged output.
  - Each session has its own `repo_path` default, sticky workspace and workspace ids: two agents that both create
    `"session"` get two workspaces. `workspace_create { shared: true }` opts into a workspace other sessions can join
    (not yet in the tool schema).
  - `workspace_list` shows a session its own and shared workspaces. `index_remove_repo` refuses while another session
    has a workspace on the repo. Closing a session deletes its own workspaces.
  - An in-process host serves the sessions; the concurrency harness drives it with `--topology in-process`.
- **Concurrent requests and a request scheduler for that host (M21 Phase 2, PHASE-21-24).** A hosted session runs up to
  4 requests at once; the host runs at most one tool call per processor and queues 64 more, first come first served,
  and answers the next one `BUSY` (new retryable error code; the stdio daemon, still one request at a time, never
  returns it). The harness takes `--max-concurrent N`.
- **One solution per repo and commit, a compilation per workspace (M21 Phase 2, PHASE-21-25).** Workspaces and
  worktrees of one repo and commit share one opened solution; each workspace compiles in its own copy with the texts
  of its own checkout, so they never see each other's edits, refreshes of different workspaces run in parallel, and
  switching between worktrees no longer reopens the solution. `IIncrementalCompiler` takes an
  `IncrementalCompileRequest` and gains `ForgetWorkspace`.
  - Bounded (T02): idle workspace copies and solutions are dropped after 10 minutes, at most 16 copies and 2 solutions
    are kept (`CompilerCacheOptions`), and every open and eviction logs a `COMPILER_CACHE` line with the counts.
  - Four agents in one in-process host now refresh in 52–66 ms at the median (was 3.9–5.3 s), with lower peak memory
    (`docs/benchmarks/phase-21-25/`).

### Known issues
- **Found by real-store tests (PHASE-21-15), documented as L-17…L-30 in `docs/KNOWN-LIMITATIONS.md`, to be
  fixed next** (L-17…L-25, L-27…L-31 are fixed above). Workspace mode: a table removed only in the workspace stays
  listed there. Implementation references (from type relations) have no file or line (L-35, found in PHASE-21-21).

### Tests
- The 39 Integration test files excluded since the SQLite store was removed are gone from the exclusion list:
  14 deleted (covered elsewhere, obsolete timing tests, or merged), 2 re-enabled, 23 rewritten against the real
  v2 store, including a real workspace stack (store + overlay store + workspace manager + merged engine, and a
  real incremental compiler over a copy of SampleSolution). Integration 205 → 297 tests (30 skipped); each test that
  shows a known issue is skipped with its bug number. The GH #6 endpoint regression test runs for the first time.
- `src/CodeMap.Storage/` and `tests/CodeMap.Storage.Tests/` (the SQLite engine, out of the solution since v2.1.0)
  are deleted.

## [2.10.0] — 2026-09-30

### Added
- **`MEMORY_SNAPSHOT` log lines** (working set, private bytes, GC heap, fragmentation, committed) after a
  baseline build and after the first overlay refresh opens the solution, in `~/.codemap/logs`
  (PHASE-21-12).
- **Idle memory is returned.** After a baseline build or the first overlay refresh, the server runs one
  compacting, memory-returning GC once no request has come for 2 minutes (ADR-061).
- **The cached overlay solution is dropped after 10 minutes without an overlay refresh.** The next refresh
  reopens it (a cold open: seconds, up to a minute on large solutions).
- The log records both as `MEMORY_SNAPSHOT reclaim_before` / `reclaim_after` / `solution_evicted`.

### Changed
- **`index_remove_repo` and `index_cleanup` require `repo_path`.** These tools delete data. They no
  longer fall back to the only / sticky registered repo; a missing `repo_path` returns
  `INVALID_ARGUMENT` naming the known repos (ADR-057, PHASE-21-10).
- **CI builds with the SDK from `global.json`** (it used .NET 9) and runs the test projects one by one, with
  the test fixtures restored and `CODEMAP_HOME` in a temp dir. There is no solution-wide `dotnet test`.
- **The tag-triggered `release.yml` is removed.** Releases are manual (`nuget-push.ps1`); a tag pushed to
  the mirror publishes nothing. DEVELOPER-GUIDE has a release checklist (PHASE-21-11).
- **`config.json`: `budget_overrides` removed.** It was parsed but never had an effect. The file now has
  two keys, `log_level` and `shared_cache_dir`. Unknown keys, including `budget_overrides` and typos,
  are ignored with one startup warning in the log (ADR-059).
- **A relative `shared_cache_dir` in `config.json` now stops startup** (exit code 2, with a message naming
  the file), as a relative `CODEMAP_CACHE_DIR` already does. Before, it was silently ignored.

### Fixed
- **A boolean parameter sent as a string (`"include_code": "false"`) failed with `INTERNAL_ERROR`.** Some MCP
  clients stringify parameters. `include_code` (`symbols_get_card`, `symbols_get_context`), `include_facts`
  (`index_diff`), `verbose` (`codemap_guide`) and `dry_run` (`index_cleanup`, `index_remove_repo`) now accept
  `true`/`false` and `"true"`/`"false"`, as numbers already did. Any other value gives the default; for `dry_run`
  that is `true`, so a malformed value never deletes anything (PHASE-21-13).
- **References written in `.razor` components, MVC views and Razor Pages were not indexed since v2.5.2.**
  The rule that skips generated files also skipped the Razor generator's output, which is the component's
  own code. `graph_callers`, `refs_find` and the call graph now include them again (L-14, ADR-062). The
  references point at the generated `obj/…_razor.g.cs` file, as the component's symbols do. The
  generator's own fields (`__tagHelperExecutionContext`, …) are not reference targets. Existing
  baselines get the references on their next build.
- **The harness golden check had compared 1 of 19 queries per sample since v2.9.0**, and still reported
  "Passed". The tool rename also renamed the harness queries, but the golden files kept the dotted names,
  and a missing golden file only counted as a skip.
  - The golden files are renamed.
  - A query that returns a result but has no golden file now fails the check.
  - `GoldenRunnerTests` runs the check on the committed samples in the Integration suite, so CI runs it.
  - Text-search goldens ignore matches in build output (`obj/`), which embed the checkout's absolute
    path; the Blazor sample's check runs again (PHASE-21-13).
  - The VB `symbols_search.prefix:Mod` golden is refreshed: members of the `Models` namespace now match
    too, and the result is truncated at the default limit.
- **`server.json` said version 1.3.2**, and the NuGet description said "26 tools" and offered a SQLite
  fallback that was removed in v2.1.0. `ReleaseMetadataTests` now checks both against the csproj
  `<Version>` and `ToolNames.All`.
- **`config.json`'s `shared_cache_dir` was never read.** It is now the shared cache when
  `CODEMAP_CACHE_DIR` is unset. Precedence: env var (whenever set; blank = disabled) > `config.json` >
  disabled (ADR-059).
- **Docs described SQLite / FTS5 and `CODEMAP_ENGINE` as current.** The engine was removed in v2.1.0.
  SYSTEM-ARCHITECTURE (storage section rewritten for the v2 engine), DEVELOPER-GUIDE, README, API-SCHEMA
  and doc comments now mention them only as history.
- **`index_cleanup` could delete the baseline under another agent's workspace.** It only knew the
  workspaces of its own process.
  - `workspace_create` now records the workspace's baseline in `overlays/<ws>/overlay.meta.json`, and
    cleanup protects those baselines for every process (`protected_by_workspaces`).
  - Workspaces created before v2.10.0 are listed in `workspaces_without_baseline_record`. A live one in
    another process makes a non-dry-run cleanup refuse (`WORKSPACE_IN_USE`).
- **Half-deleted baselines.** Both tools used to delete recursively and ignore errors. On Windows a
  baseline open in this or another process was left gutted and then failed with "incomplete (missing
  segments)".
  - A baseline is now removed only when none of its files is open. It is renamed aside in one step,
    then deleted.
  - A baseline in use stays complete and is listed in `skipped_in_use`. Counts include only what was
    removed.
- **`index_remove_repo` also deleted every workspace overlay of the repo without saying so.**
  - Overlays removed are now listed in `workspaces_removed`.
  - If another process has a workspace open, the call returns `WORKSPACE_IN_USE` and deletes nothing.
  - This process's own workspaces are closed and deleted properly, and the sticky default is cleared.
- `index_cleanup` removes the baseline-level overlay (`overlays/<commit sha>/`) of a baseline it removes.
  It used to leave it behind.

## [2.9.0] — 2026-09-29

### Changed
- **Tool names use underscores: `symbols_search`, `graph_callers`, `index_ensure_baseline`, …**
  (all 28; the mapping replaces the first `.` with `_`). LLM APIs require tool names matching
  `^[a-zA-Z0-9_-]{1,128}$`, so every client had to rewrite the dotted names, each in its own way.
  - `tools/list` returns only the new names.
  - Every tool name in responses uses the new names: `next_actions`, answers, hints, errors,
    `codemap_guide`.
  - **Claude Code users see no change:** Claude Code already showed `mcp__codemap__symbols_search`.
  - README, CLAUDE-INSERT, agent guide, API-SCHEMA and GETTING-STARTED use the new names (ADR-051,
    ADR-056, PHASE-21-09).

### Deprecated
- **The dotted names (`symbols.search`, …) are deprecated aliases,** kept until **v2.11.0 at the
  earliest**.
  - A call with a dotted name returns the same result as the new name, plus a second text content
    item naming the new tool. The server logs one warning per alias.
  - Aliases aren't listed in `tools/list`. Clients that check names against the list (e.g. MCP
    Inspector) use the new names after reconnecting. The aliases are for clients with a cached tool
    list, scripts and direct JSON-RPC callers.
  - If your project's CLAUDE.md contains the CodeMap block, refresh it from `docs/CLAUDE-INSERT.MD`.

### Fixed
- `CLAUDE-INSERT.MD` / `CODEMAP-CLAUDE-MD-BLOCK.MD` no longer advertise `surfaces.list_di_registrations`,
  which was never a registered tool. DI registrations are in the `codemap_summarize` DI section.
- `docs/GETTING-STARTED.MD` no longer describes the removed SQLite storage (`.db` baselines, `CODEMAP_ENGINE=sqlite`):
  - it shows the v2 `store/` layout and `CODEMAP_HOME`;
  - the tool count is 28, with the missing `index_remove_repo` / `codemap_guide` rows added;
  - the prerequisite is .NET 10.

## [2.8.2] — 2026-09-29

### Added
- **`CODEMAP_HOME` environment variable** relocates the data root (`config.json`,
  `logs/`, `store/` baselines + overlays, `_savings.json`) away from `~/.codemap`.
  Accepts an absolute path or `~/…`; a relative path fails startup with exit
  code 2 and a one-line stderr message. Unset = unchanged behaviour. Doesn't
  move the install layout (`bin/`) or the `CODEMAP_CACHE_DIR` shared cache.
  Groundwork for the multi-agent concurrency benchmark (PHASE-21-01, ADR-039).
- **Error codes `WORKSPACE_IN_USE`, `STORAGE_ERROR` (both `retryable: true`) and
  `INTERNAL_ERROR`.** `WORKSPACE_IN_USE` is returned when another CodeMap process
  holds the workspace (its overlay WAL is locked) and tells the agent to retry or use a
  different `workspace_id`; `details.workspace_id` names it (ADR-041).

### Benchmarks
- Phase 0 multi-agent baseline (PHASE-21-06): today's one-process-per-agent model measured at
  N = 1, 4, 8, 16 agents on five repos (`docs/benchmarks/phase-21-06/`). Memory grows linearly with
  N (≈ 2.5–3.9 GB per process on large solutions), and every agent builds the same baseline: cold build
  on Bitwarden 79 s alone → 178 s each at N=16. Inputs to the shared-host design (ADR-048…053, accepted).
- Harness: `--repo-name`, memory guard (`--max-memory-gb`, skipped runs recorded), per-run failure
  capture, `concurrency-summary`, worktree restore (`--no-restore`), long-path clones.

### Changed
- **Error codes now describe the actual failure** (ADR-041). Previously every
  `index.ensure_baseline` failure was `COMPILATION_FAILED` — including validation errors
  such as "solution_path not found" (now `INVALID_ARGUMENT`) and storage/I-O failures
  (now `STORAGE_ERROR`). Every failure in `workspace.create/reset/list/delete`,
  `index.refresh_overlay` and `repo.status` was `INVALID_ARGUMENT` — I/O conflicts are now
  `WORKSPACE_IN_USE` / `STORAGE_ERROR`, unexpected exceptions `INTERNAL_ERROR`. Invalid
  caller input is still `INVALID_ARGUMENT`, and a real compile failure still
  `COMPILATION_FAILED`. Clients that branched on the old codes for these cases will see
  the new, more specific ones; `retryable` is a code-independent retry signal.
- **An exception escaping any tool now returns a CodeMap error, not JSON-RPC `-32603`** (ADR-045).
  Previously a failure in a read-only tool (`symbols.*`, `refs.find`, `graph.*`, …) that its handler
  didn't catch arrived as a protocol error with no code. It now comes back as the usual error
  envelope: `WORKSPACE_IN_USE` / `STORAGE_ERROR` (retryable) for file conflicts and storage failures,
  otherwise `INTERNAL_ERROR` with `details.exception_type`. `-32603` is now only used for
  protocol-level failures.
- **Roslyn 5.3.0 → 5.9.0** (`Microsoft.CodeAnalysis.*`), so the source generators of current .NET
  SDKs (10.0.4xx, whose Razor compiler needs Roslyn 5.9) load again. Blazor/Razor components,
  `@code` members and `@page` routes are indexed on those SDKs (F10). Older SDKs (10.0.2xx) keep
  working; no extraction change on CodeMap's own solution or the golden samples.
  `Microsoft.Extensions.*` 9.0.2 → 10.0.1 (required by Roslyn 5.9's dependencies).
- **Overlays moved to `store/<repoId>/overlays/<workspaceId>/`** (was `store/overlays/<workspaceId>/`,
  ADR-043). Overlays are per-session scratch state and are not migrated: a workspace that was
  open across the upgrade starts from an empty overlay when re-created (run
  `workspace.create` again). The old `store/overlays/` tree is ignored; `index.cleanup` removes
  it, skipping any overlay a still-running older daemon holds. No baseline change, no re-index.

### Fixed
- **Concurrent indexing could corrupt a published baseline (F6).** When two
  CodeMap processes indexed the same commit at the same time — e.g. several agents
  each running their own `codemap-mcp` — the second publisher deleted the first's
  baseline before moving its own in. On Windows that deletion stopped part-way at
  a memory-mapped file, leaving a baseline with missing segments that could then be
  served (empty or partial results) or fail every later rebuild; surfaced as
  `COMPILATION_FAILED: … being used by another process` or `… already exists`.
  Publication is now non-destructive and atomic: an existing complete baseline is
  adopted, incomplete ones are moved aside and rebuilt, and a baseline with missing
  segments is never served. The shared-cache pull/push (`CODEMAP_CACHE_DIR`) had the
  same delete-then-move pattern and is fixed the same way (ADR-040). No format change,
  no re-index.
- **Several agents indexing the same checkout could produce a degraded baseline (F9).**
  Two CodeMap processes evaluating one checkout at the same time raced MSBuild on
  generated files under `obj/` ("Could not write lines to file … already exists"), and
  the losing compilation — missing those files, with compile errors — was published as a
  normal baseline for that commit. MSBuild evaluation of a checkout is now exclusive
  across CodeMap processes (a lock file under the temp directory, keyed by checkout
  root); a load that still hits a transient file conflict (e.g. with an IDE build) is
  retried once. Separate worktrees/clones are unaffected (ADR-042).
- **The same `workspace_id` in two repositories shared one overlay.** Overlay storage was keyed
  by workspace id alone, so with `workspace_id: "session"` in two repos, even within one CodeMap
  process, the second repo's workspace reused the first repo's overlay: edits indexed in one
  repo showed up in the other, lookups resolved against the wrong baseline, and deleting or
  resetting one repo's workspace wiped the other's. Across processes the second repo got
  `WORKSPACE_IN_USE` although the repos were unrelated. Overlays are now scoped by repository
  (ADR-043).
- **Linux/macOS: two CodeMap processes could write the same workspace overlay and silently lose
  edits (F4).** On Unix, file sharing is advisory, so a second process opening a workspace already
  open in another process (e.g. two agents in one repo, both using `workspace_id: "session"`) got
  no error. Both wrote to the same overlay, and whichever shut down last discarded the other's
  indexed edits. Each overlay now holds an exclusive `overlay.lock` while open, so the second
  process gets `WORKSPACE_IN_USE` (retryable) on every OS, as it already did on Windows (ADR-044).
- **Concurrent indexing could move a freshly published baseline aside.** When several processes
  published the same baseline at once, one could quarantine the winner's complete baseline and
  republish (seen under load on Linux; possible on any OS). Harmless for data, since nothing was
  deleted, but wasted work that could fail a publisher. The publisher now re-checks before
  quarantining (ADR-044).
- **Blazor/Razor components could vanish from the index at full confidence (F10).** When the
  installed .NET SDK is newer than CodeMap's bundled Roslyn (e.g. SDK 10.0.4xx), the SDK's Razor
  source generator can't be loaded. Its output (component classes, `@code` members, `@page` routes)
  was then silently missing, while `semantic_level` said `full`. This is now reported:
  `project_diagnostics[].generator_load_failures` names the generator and both compiler versions,
  `semantic_level` becomes `partial`, confidence `medium`, and a warning names the fix (ADR-046,
  KNOWN-LIMITATIONS L-11). Affects every OS and v2.8.1. A baseline built silently degraded before
  the upgrade stays incomplete until the next commit or `index.remove_repo`.
- **Queries reported `semantic_level: full` for baselines the build had marked `partial` (F13).**
  The baseline manifest kept only each project's name, `compiled` flag and counts, and the store
  recomputed the level from `compiled` alone. So a degradation detected at build time (e.g. the F10
  generator failures above) appeared in the `index.ensure_baseline` response and nowhere else. The
  manifest now keeps `errors`, `target_frameworks` and `generator_load_failures`, and the build and
  the store share one level rule (`SemanticLevels`, ADR-054). Additive `manifest.json` fields, with no format
  change. Baselines written by older versions lack them and read as before.
- **An unrestored checkout was indexed with unresolved references at `semantic_level: full` (F12).**
  A fresh clone or git worktree has no `obj/project.assets.json` (gitignored), and CodeMap doesn't
  restore, so package references (and sometimes the target framework itself) were unresolved. On
  eShopOnWeb that meant 15,158 compile errors, 40% fewer references and 24% fewer symbols, all reported as
  `full`. Now each such project reports `project_diagnostics[].missing_restore_output`, and with compile
  errors the run is `partial` (in `index.ensure_baseline` and every query) and confidence is `medium`. A
  warning says to run `dotnet restore` and rebuild the baseline, and the baseline isn't pushed to a
  shared cache (ADR-054, KNOWN-LIMITATIONS L-12). A baseline built before the restore stays degraded
  until the next commit or `index.remove_repo`.
- **`symbols.search` with `OR` or quotes returned nothing** (every version since v2.1.0). The tool
  description and the agent guide recommend `Foo OR Bar`, but the v2 search engine treated `OR` as one
  more word that had to match, and it kept double quotes inside the search terms, so both always
  returned 0 hits. `OR` now separates alternatives (lowest precedence, each alternative ranked on its
  own), quoted words mean "all of these words", and workspace-mode search parses queries the same way
  as committed mode (before, `Order*` or quotes found no overlay symbols). `NOT` / `NEAR` now return
  `INVALID_ARGUMENT` instead of silently matching nothing. The tool description and empty-result hint
  no longer describe SQLite FTS5 syntax (ADR-055). Query-time only; no re-index.
- **`symbols.search` with two or more `kinds` lost results and misreported `truncated`.** The kinds
  were filtered *after* the result had been cut to `limit` across all kinds, so when symbols of other
  kinds scored higher, the requested kinds were crowded out (often 0 hits, `truncated: false`). The
  filter now runs before scoring and the limit, so the page and `truncated` are exact (the same
  principle as the v2.8.1 `list_endpoints` fix). A single kind and browse-by-kinds were not affected.
- **A blank `CODEMAP_CACHE_DIR` wrote baselines into the working directory (F11).** An empty
  value (e.g. `"CODEMAP_CACHE_DIR": ""` in an MCP client config) was treated as an enabled shared
  cache at a relative path, so each new baseline was copied into the directory the server was
  started from, usually the repo root, as an untracked `local-<hash>/` folder. A blank value now
  disables the cache; a relative value is rejected at startup with a one-line message and exit
  code 2, like `CODEMAP_HOME` (ADR-047). If you find a stray `local-<hash>/` or
  `<remote-hash>/` folder of baseline files in a repo, it's safe to delete.
- **The `Dockerfile` did not build** (it used the .NET 9 SDK image and copied the removed
  `CodeMap.Storage` project). It now uses `sdk:10.0` and the current project set.

### Internal
- Linux test rig: `tests/docker/run-linux-tests.ps1` runs the storage, MCP, build-lock and
  concurrency suites plus harness measurements in a Linux container (DEVELOPER-GUIDE). It now
  restores the testdata fixtures and has `integration` (whole Integration project) and
  `restore-probe` suites.
- `McpSubprocessTests` spawned the **installed** daemon (`~/.codemap/bin`) against the live data root,
  so every Integration run rewrote the developer's `~/.codemap/_savings.json` and tested the installed
  version instead of the branch. They now run the build-output daemon with a per-test `CODEMAP_HOME`.

## [2.8.1] — 2026-08-07

### Fixed
- **`surfaces.list_endpoints` dropped every non-`DELETE` verb on large repos
  ([GH #6](https://github.com/bbajt/csharp-code-map/issues/6)).** The query
  fetched only `limit + 1` route facts from storage — which returns them
  ordered alphabetically by value (`"METHOD /path"`) — and applied the
  `path_filter` / `http_method` filter *afterward*. Because `DELETE` sorts
  before `GET` / `PAGE` / `PATCH` / `POST` / `PUT`, on any solution with more
  routes than the limit the fetch window was entirely `DELETE`, so every other
  verb (and most `path_filter` queries) returned zero with high confidence.
  Extraction was never at fault. The fix fetches the complete route-fact set,
  filters, *then* applies the limit — so filtering always precedes truncation.
  The store already materializes the full fact set for a kind internally, so
  this adds no I/O.
- **`surfaces.list_config_keys` shared the identical latent bug** (same
  fetch-before-filter pattern on `key_filter`) and is fixed the same way.
- Truncation reporting is now **exact**: `truncated` is `true` if and only if
  more matches existed than the returned page — no more false positives from
  raw facts the filter discarded.
- Workspace/overlay path (`MergedQueryEngine`) fixed for both surfaces: overlay
  facts and the baseline set are fetched in full, merged (overlay-wins-by-file),
  then limited once.

### Internal
- New `QueryEngine.PageAfterFilter` helper + `FetchAllFacts` sentinel unify the
  filter-before-limit contract across the surface-list methods. `ListDbTablesAsync`
  already followed this pattern and is unchanged.
- Regression coverage: query-level unit tests reproducing the alphabetical-window
  miss (endpoints + config keys), a `MergedQueryEngine` workspace test, and a
  real-store (`EndpointSurfaceIntegrationTests`) E2E seeding 60 `DELETE` + 6 `GET`
  routes to prove the verb/path filters through actual SQLite `ORDER BY … LIMIT`.

### Credits
- Reported by [@artisvilcins](https://github.com/artisvilcins) (GH #6) with a
  precise repro (38-project solution, `ChatController` verb matrix) that made the
  root cause diagnosable.

## [2.8.0] — 2026-06-11

### Added
- MCP 2025-03-26 tool annotations on `tools/list`:
  `readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`
  on every shipped tool. Compliant clients (Claude Desktop and similar)
  auto-approve read-only calls and gate destructive ones.
- `ToolAnnotations` record + optional `Annotations` param on
  `ToolDefinition`. Three presets in `HandlerHelpers`: `AnnotReadOnly`,
  `AnnotWriteIdempotent`, `AnnotDestructIdempotent`.

### Changed
- All 28 tools classified: 21 ReadOnly, 3 WriteIdempotent
  (`index.ensure_baseline`, `index.refresh_overlay`, `workspace.create`),
  4 DestructIdempotent (`index.cleanup`, `index.remove_repo`,
  `workspace.delete`, `workspace.reset`).

### Compatibility
- Fully backward-compatible: the `annotations` key is emitted only when
  set, so clients that don't understand the field see no spurious data.

### Credits
- Inspired by [GH PR #1](https://github.com/bbajt/csharp-code-map/pull/1)
  by [@igorsamoylenko](https://github.com/igorsamoylenko). Re-implemented
  in the canonical working repo with three substantive adjustments:
  `Idempotent: true` on every preset (CodeMap mutating ops are
  observationally idempotent — clients must be able to retry transient
  failures); `workspace.reset` classified as Destruct (discarding overlay
  revisions is irreversible); `OpenWorld` defaults to `false` on the
  record itself (CodeMap-specific closed-world override of the spec
  default).

## [2.7.1] — 2026-06-11

### Fixed
- `TypeCardHint` did not fire when `symbols.get_card` /
  `symbols.get_context` was called with `include_code: false` —
  metadata-only callers silently lost the member-enumeration hint.
- `EmptySearchHint` wording said "name_filter" but `symbols.search`
  uses top-level filter fields. Reworded as generic "filters."

## [2.7.0] — 2026-06-11

### Added
- Five agent-recovery hints across MCP responses and errors:
  empty `symbols.search` nudge; Type-card → member enumeration pointer;
  unknown-tool / unknown-method Levenshtein "did you mean"; `NOT_FOUND`
  fuzzy candidates (real search + up to 3 inline symbol IDs with
  file:line). All hints ride in the existing `answer` field or error
  `message` — no envelope shape change.

## [2.6.3] — 2026-06-11

### Fixed
- `RecordToCoreMappings.ToSymbolCard` hardcoded `Confidence.High` for
  every baseline symbol regardless of extraction quality. Now downgrades
  to `Confidence.Low` when `FileIntId == 0` (the syntactic-fallback
  sentinel produced when a project fails to compile cleanly). Read-time
  only — no binary format change, no re-index required.

## [2.6.2] — 2026-05-27

### Fixed
- Workspace-mode `symbols.search` / `graph.callers` threw JSON-RPC
  `-32603` when an overlay-new symbol had an empty FQN
  (`SymbolId.From("")` throws). Read+write guards added in
  `CustomEngineOverlayStore`. Existing on-disk overlays remain safe.
- `-32603` errors now carry `error.message: "Internal error: <ex.Message>"`
  and `error.data: { exceptionType, method }` for diagnosability.

## Earlier versions

See git log and tagged releases for v2.6.1 and earlier.
