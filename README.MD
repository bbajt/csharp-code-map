<!-- mcp-name: io.github.bbajt/codemap-mcp -->
# CodeMap — Turn Your AI Agent Into a Semantic Dragon

[![NuGet](https://img.shields.io/nuget/v/codemap-mcp.svg?label=nuget&color=blue)](https://www.nuget.org/packages/codemap-mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/codemap-mcp.svg?color=blue)](https://www.nuget.org/packages/codemap-mcp)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](#)
[![MCP Server](https://img.shields.io/badge/MCP-server-1f6feb)](#)
[![C%23](https://img.shields.io/badge/C%23-Roslyn-239120)](#)
[![VB.NET](https://img.shields.io/badge/VB.NET-Roslyn-512BD4)](#)
[![F%23](https://img.shields.io/badge/F%23-FCS-378BBA)](#)
[![GitHub Stars](https://img.shields.io/github/stars/bbajt/csharp-code-map?style=social)](https://github.com/bbajt/csharp-code-map/stargazers)
[![License](https://img.shields.io/badge/license-Non--Commercial-orange.svg)](LICENSE)

> *Stop feeding your AI agent raw source files. Give it a semantic index instead.*

CodeMap is a **Roslyn-powered MCP server** that lets AI agents navigate **C#, VB.NET, and F#** codebases by **symbol, call graph, and architectural fact** — instead of brute-reading thousands of lines of source code. One tool call. Precise answer. No context flood.

**Average token savings: 90%+** versus reading files directly.

---

## Install via Claude Code or manually

The fastest way to install is to paste the prompt below into a **Claude Code** shell. Claude will check your environment, install the tool, and register it as an MCP server — no manual steps needed.

> Check whether .NET 10 SDK is installed by running `dotnet --version`. If the reported version is below 10.0, install it: on Windows run `winget install Microsoft.DotNet.SDK.10`, on macOS/Linux download from https://dotnet.microsoft.com/download/dotnet/10.0. Verify with `dotnet --version` once done. Once .NET 10 is confirmed, install CodeMap: if `codemap-mcp` is not yet installed run `dotnet tool install --global codemap-mcp`, otherwise run `dotnet tool update --global codemap-mcp` to get the latest version. Verify the binary is reachable with `codemap-mcp --version`. Finally, register it as a global MCP server in Claude Code by running `claude mcp add codemap-mcp codemap-mcp --scope user` and confirm it appears in the output of `claude mcp list`.

**Or install manually:**

```bash
# Install .NET 10 SDK if needed (Windows)
winget install Microsoft.DotNet.SDK.10

dotnet tool install --global codemap-mcp
codemap-mcp --version
claude mcp add codemap-mcp codemap-mcp --scope user
```

Requires **.NET 10** (LTS). If you're working on a C# or VB.NET codebase you almost certainly have it already — check with `dotnet --version`.

### Upgrading to v2.10.0

No re-index and no index format change. What you may notice:

- **`index_cleanup` and `index_remove_repo` need `repo_path`.** They delete data, so they no longer fall back to
  the only registered repo. Cleanup now protects the baseline of every workspace in every CodeMap process, and
  a baseline that is in use is never half-deleted (it's reported in `skipped_in_use`).
- **References from Blazor components, MVC views and Razor Pages are back** (missing since v2.5.2).
  `graph_callers` / `refs_find` on a service or model now include uses written in `.razor` / `.cshtml` files.
  They appear when a baseline is next built (any new commit).
- **Idle memory is returned.** After indexing, an idle server gives most of the build's memory back
  (nopCommerce: 2.9 GB → 1.1 GB after 2.5 minutes, 0.2 GB after 13 minutes).
- **`config.json` has two keys, `log_level` and `shared_cache_dir`.** `shared_cache_dir` now works (it was
  never read); `budget_overrides` is gone (it never had an effect). Unknown keys give one warning in the log.
  A relative `shared_cache_dir` stops startup, as a relative `CODEMAP_CACHE_DIR` does.
- **Boolean parameters sent as strings** (`"include_code": "false"`) work instead of returning `INTERNAL_ERROR`.

Details: [CHANGELOG](CHANGELOG.MD) `[2.10.0]`.

### Upgrading to v2.9.0: new tool names

Tool names use underscores now (`symbols_search` instead of the dotted form), because LLM APIs don't allow dots in tool names. **Claude Code users see no change**: Claude Code already showed `mcp__codemap__symbols_search`. The old dotted names keep working until v2.11.0 at the earliest, for clients and scripts that still send them. Each such call adds a note naming the new tool. If your project's CLAUDE.md contains the CodeMap block, refresh it from [`docs/CLAUDE-INSERT.MD`](docs/CLAUDE-INSERT.MD).

### Upgrading from v1.x

v2.0.0 uses a new binary storage engine (memory-mapped segments instead of SQLite). Your old `.db` baselines are not migrated — run `index_ensure_baseline` once per repo to rebuild. The old `~/.codemap/baselines/` folder is unused and can be deleted. The SQLite engine was removed in v2.1.0.

---

## The Problem

An AI agent working on a C# codebase without CodeMap does this:

```
Agent: I need to find who calls OrderService.SubmitAsync.
→ Read OrderService.cs       (3,600 tokens)
→ Read Controllers/...       (3,600 tokens)
→ Grep across src/           (another 3,600 tokens)
→ Maybe find it. Maybe not.
```

With CodeMap:

```
refs_find { symbol_id: "M:MyApp.Services.OrderService.SubmitAsync", kind: "Call" }
→ 220 tokens. Exact file, line, and excerpt for every call site. Done.
```

That's **93.9% fewer tokens** for a task agents do dozens of times per session. On a real production codebase (100k+ lines), savings are **95–99%+**.

---

## What It Does

CodeMap builds a **persistent semantic index** from your solution file using Roslyn — the same compiler that powers Visual Studio. Supports both **`.sln`** (all Visual Studio versions) and **`.slnx`** (VS 2022 17.12+ / .NET SDK 9+) solution formats — **auto-discovered** when `solution_path` is omitted (prefers `.slnx`). Short commit SHAs are auto-expanded. The index captures:

- Every symbol (classes, methods, properties, interfaces, records)
- Every call relationship and reference (who calls what, where)
- Type hierarchy (inheritance chains, interface implementations)
- Architectural facts extracted from code: HTTP endpoints, config keys, DB tables, DI registrations, middleware pipeline, retry policies, exception throw points, structured log templates

All of this is exposed via **28 MCP tools** that any MCP-compatible AI agent can call. Starting from v1.3, CodeMap also navigates **DLL boundaries** — lazily resolving NuGet and SDK symbols on first access, with optional ICSharpCode.Decompiler source reconstruction and cross-DLL call graphs.

**Supported languages:** C#, VB.NET, and F#. Mixed-language solutions (`.sln` / `.slnx` containing C#, VB.NET, and F# projects) are indexed in a single pass. All 28 MCP tools work identically for symbols from any language. C# and VB.NET use Roslyn's `MSBuildWorkspace`; F# uses `FSharp.Compiler.Service` (MSBuildWorkspace doesn't support `.fsproj`). F# architectural fact extractors (endpoints, DI, config) are not yet implemented — symbol search, call graphs, references, and type hierarchy all work.

**Blazor / Razor (v2.5.0+):** `.razor` components are indexed via the Razor source generator. ComponentBase-derived classes appear in `symbols_search`. `@page` routes surface in `surfaces_list_endpoints` with a `PAGE` HTTP method. `[Inject]` and `[Parameter]` properties emit dedicated `RazorInject` / `RazorParameter` facts. Since v2.10.0, code written in components, MVC views and Razor Pages is part of the reference graph: `graph_callers` on a service method shows the component or view that calls it. The generator's own scaffolding (tag-helper plumbing) is left out.

**Multi-target projects (v2.5.1+):** `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>` previously produced one extraction per TFM (3× duplication). CodeMap now collapses to a single extraction on the highest-ranked TFM, with `ProjectDiagnostic.TargetFrameworks` listing every TFM in the group. Symbol counts on heavily multi-targeted Blazor libraries drop 60–80%.

**Interface-aware `graph_callers` (v2.6.0+):** in DI-dispatched codebases (most production .NET) `graph_callers` on a concrete method silently under-reported because real call sites resolve through the registered interface. CodeMap now detects interface implementation at query time and surfaces an `interface_implementation_hint` listing the interface members and an estimated count of additional callers routed through them. Pass `follow_interface: true` to union those into the result (deduped by `from_symbol`). No baseline-format change, no re-index required. Handles both implicit and explicit interface implementations.

**Indexing perf + correctness (v2.5.2):** large reduction in indexing wall-clock by skipping auto-generated trees (`*.g.cs`, `*.Designer.cs`, files with `<auto-generated>`, paths under `obj/`), short-circuiting type-position identifier classification (typeof / generic args / base lists / attributes), and parallelizing Pass-2 reference & fact extraction across projects. Validated on a 9-repo Blazor corpus: Blazorise drops from 408 s → 95 s (−77 %), ant-design-blazor from 47 s → 25 s (−47 %), OrchardCore (single-target sentinel) from 131 s → 96 s (−27 %), and a 78-csproj distributed-database project (ByTech.Bedrock) indexes in 27 s with an 11.2× Pass-2 parallel speedup. Five query-correctness bugs also fixed: `symbols_search` browse-by-kinds now honours `namespace` / `file_path` / `project_name` filters; workspace-mode namespace filter is case-insensitive (matches committed mode); `refs_find` cache key includes `resolution_state`; workspace browse-by-kinds now includes overlay-new symbols; `codemap_guide`'s decision table no longer advertises `surfaces_list_di_registrations` (which was never a registered tool).

---

## The Transformation

Here's what changes when you give an agent CodeMap:

| Without CodeMap | With CodeMap |
|---|---|
| `grep -rn "OrderService" src/` | `symbols_search { query: "OrderService" }` |
| Read 5 files to understand a method | `symbols_get_context` — card + source + all callees in one call |
| Manually trace call chains across files | `graph_trace_feature` — full annotated tree, one call |
| Hope grep finds the right interface impl | `types_hierarchy` — base, interfaces, derived types, instant |
| Read the whole file to find config usage | `surfaces_list_config_keys` — every `IConfiguration` access, indexed |
| Diff two commits by reading changed files | `index_diff` — semantic diff, rename-aware, architectural changes only |

The agent stops **reading** your codebase and starts **understanding** it.

---

## Showpiece: `graph_trace_feature`

The most powerful tool. Replaces 5–10 manual calls with one:

```json
graph_trace_feature {
  "repo_path": "/path/to/repo",
  "entry_point": "M:MyApp.Controllers.OrdersController.Create",
  "depth": 3
}
```

Returns an annotated call tree with architectural facts at every node:

```
OrdersController.Create  [POST /api/orders]
  → OrderService.SubmitAsync
      → [Config: App:MaxRetries]
      → [DI: IOrderService → OrderService | Scoped]
      → Repository<Order>.SaveAsync
            → [DB: orders | DbSet<Order>]
            → [Retry: WaitAndRetryAsync(3) | Polly]
```

One query. Full feature flow. Every config key touched, every table written, every retry policy applied — surfaced automatically from the index.

---

## Token Savings Benchmark

Measured across 24 canonical agent tasks on a real .NET solution:

| Task | Raw Tokens | CodeMap | Savings |
|------|-----------|---------|---------|
| Find a class by name | 3,609 | 248 | **93%** |
| Get method source + facts | 3,609 | 336 | **91%** |
| Find all callers (refs_find) | 3,609 | 220 | **94%** |
| Caller chain depth=2 | 3,609 | 287 | **92%** |
| Type hierarchy | 3,609 | 200 | **94%** |
| List all HTTP endpoints | 3,609 | 360 | **90%** |
| List all DB tables | 3,609 | 169 | **95%** |
| Workspace staleness check | 3,609 | 62 | **98%** |
| Baseline build (cache hit) | ~30s Roslyn | ~2ms pull | **∞** |
| **Average** | | | **90.4%** |

*Raw tokens = reading all source files. On production codebases (100k+ lines), savings reach 95–99%+.*

Run it yourself:
```bash
dotnet test --filter "Category=Benchmark" -v normal
```

---

## 28 Tools Across Six Categories

### Discover
| Tool | What it does |
|------|-------------|
| `symbols_search` | Search by name (prefix match, `OR`), kind, namespace, or file path |
| `code_search_text` | Regex/substring search across source files — returns file:line:excerpt |
| `symbols_get_card` | Full symbol metadata + architectural facts + source code |
| `symbols_get_context` | Card + source + all callees with source — deep understanding in one call |
| `symbols_get_definition_span` | Raw source only, no overhead |
| `code_get_span` | Read any source excerpt by line range |

### Navigate
| Tool | What it does |
|------|-------------|
| `refs_find` | All references to a symbol, classified (Call, Read, Write, Implementation…) |
| `graph_callers` | Depth-limited caller graph — who triggers this? |
| `graph_callees` | Depth-limited callee graph — what does this orchestrate? |
| `graph_trace_feature` | Full annotated feature flow with facts at every node |
| `types_hierarchy` | Base type, interfaces implemented, and all derived types |

### Architecture
| Tool | What it does |
|------|-------------|
| `codemap_summarize` | Full codebase overview: endpoints, DI, config, DB, middleware, logging |
| `codemap_export` | Portable context dump (markdown/JSON, 3 detail levels) for any LLM |
| `codemap_guide` | Quick-start guide: session setup, decision table, and usage rules for agents |
| `index_diff` | Semantic diff between commits: symbols added/removed/renamed, API changes |
| `surfaces_list_endpoints` | Every HTTP route (controller + minimal API) with handler and file:line |
| `surfaces_list_config_keys` | Every `IConfiguration` access with usage pattern |
| `surfaces_list_db_tables` | EF Core entities + `[Table]` attributes + raw SQL table references |

### Workspace
| Tool | What it does |
|------|-------------|
| `workspace_create` | Isolated overlay for in-progress edits |
| `workspace_reset` | Clear overlay, back to baseline |
| `workspace_list` | All active workspaces with staleness, SemanticLevel, and fact count |
| `workspace_delete` | Remove a workspace |
| `index_refresh_overlay` | Re-index changed files incrementally (~63ms) |

### Index Management
| Tool | What it does |
|------|-------------|
| `index_ensure_baseline` | Build the semantic index (idempotent, cache-aware, auto-discovers solution) |
| `index_list_baselines` | All cached baselines with size, age, and commit |
| `index_cleanup` | Remove stale baselines (dry-run default; `repo_path` required; never HEAD or any workspace's baseline) |
| `index_remove_repo` | Remove ALL baselines and workspaces of a repo (dry-run default; `repo_path` required; refuses while another process has a workspace open) |

### Repo
| Tool | What it does |
|------|-------------|
| `repo_status` | Git state + whether a baseline exists for current HEAD |

---

## Workspace Mode — See Your Own Edits

CodeMap tracks uncommitted changes via an **overlay index**. Every agent session gets its own isolated workspace:

```
1. index_ensure_baseline   → index HEAD once
2. workspace_create        → agent gets isolated overlay
3. Edit files on disk
4. index_refresh_overlay   → re-indexes only changed files (~63ms)
5. Query with workspace_id → results include your in-progress code
```

Three consistency modes:
- **Committed** — baseline index only (default, no workspace needed)
- **Workspace** — baseline + your uncommitted edits merged
- **Ephemeral** — workspace + virtual file contents (unsaved buffer content)

---

## Multi-Agent Supervisor Support

Running multiple agents in parallel? What works today:

- Each workspace is isolated: agents that use different `workspace_id`s never see each other's edits
- `workspace_list` shows every workspace: `IsStale`, `SemanticLevel`, fact count
- Stale detection fires when a workspace's base commit diverges from HEAD
- Supervisor can inspect, clean up, or re-provision any agent's workspace

What to know (measured in the multi-agent benchmark, `docs/CODEMAP-MULTI-AGENT-SCALING-FINDINGS.md`):

- **One CodeMap process per agent.** Each MCP client or sub-agent starts its own server, so memory
  grows with the number of agents: roughly 0.3–0.5 GB per process on small and medium solutions,
  2.5–4 GB on large ones. Every process builds the same baseline itself the first time, and concurrent
  builds slow each other down. A shared host is the planned fix (ADR-048).
- **A workspace can't be shared across processes.** If another CodeMap process holds that
  `workspace_id`, the call returns `WORKSPACE_IN_USE` (retryable): retry, or give each agent its own ID.
- **Agents in fresh worktrees must restore first.** A new clone or `git worktree add` has no restore
  output; run `dotnet restore` before `index_ensure_baseline`, or the index is incomplete (reported as
  `semantic_level: partial`, L-12).
- **Cleanup is safe across agents (v2.10.0).** `index_cleanup` never removes a baseline that any agent's
  workspace uses, in any process, and never deletes a baseline another process has open. `index_remove_repo`
  refuses (`WORKSPACE_IN_USE`) while another process has a workspace open in the repo.
- **An idle process gives memory back (v2.10.0).** Two minutes after indexing, the server compacts its heap;
  after 10 minutes without an overlay refresh it also drops the cached Roslyn solution (the next refresh
  reopens it). This lowers each process's idle cost; the per-process multiplication stays until the shared host.

---

## Self-Healing Under Broken Builds

When a file doesn't compile, CodeMap doesn't drop references. It stores **unresolved edges** with syntactic hints. When compilation succeeds again (after a fix), a **resolution worker** automatically upgrades them to fully-resolved semantic edges.

`refs_find` returns both. Filter with `resolution_state: "resolved"` if you need certainty.

---

## DLL Boundary Navigation

CodeMap resolves DLL symbols lazily on first agent access — `NOT_FOUND` at a DLL
boundary triggers automatic extraction rather than a dead end.

**Two levels, both permanent (cached in baseline DB):**

| Level | Trigger | What you get | Cost |
|-------|---------|--------------|------|
| 1 — Metadata stub | Any `NOT_FOUND` query | Method signatures, XML docs, type hierarchy | ~1–5ms (once) |
| 2 — Decompiled source | `symbols_get_card` with `include_code: true` | Full reconstructed C# source via ICSharpCode.Decompiler | ~10–200ms (once) |

After Level 2, cross-DLL call graph edges are extracted so `graph_callees` and
`graph_trace_feature` traverse INTO and THROUGH DLL code seamlessly.

**`source` discriminator in `symbols_get_card` response:**
- `"source_code"` — symbol is from your own source
- `"metadata_stub"` — Level 1 only (decompilation unavailable)
- `"decompiled"` — Level 2 source reconstructed and ready

`graph_trace_feature` applies a `max_lazy_resolutions_per_query` budget (default 20)
when encountering previously-unseen DLL types to bound decompilation latency.

---

## Shared Baseline Cache

Index once, reuse everywhere — across machines, CI, Docker containers:

```bash
export CODEMAP_CACHE_DIR=/shared/codemap-cache
```

or, in `config.json` (see Observability): `{ "shared_cache_dir": "/shared/codemap-cache" }`.

- `index_ensure_baseline` pulls from cache first (~2ms vs ~30s Roslyn build)
- Auto-push after every new baseline build, except baselines this machine degraded (an unrestored
  checkout or a source generator that failed to load), since another machine would get an incomplete index
- Self-healing: corrupt cache entries are detected and overwritten
- Precedence: `CODEMAP_CACHE_DIR` (whenever set; blank = disabled) > `config.json` `shared_cache_dir` >
  disabled. With neither, all cache ops are no-ops
- Must be an absolute path or start with `~/`; a relative value stops the server at startup
  (exit code 2), since it would resolve against the client's working directory

---

## v2 Storage Engine — 10x Faster Queries

v2.0.0 replaces SQLite with a **custom binary storage engine** using memory-mapped segment files. The Roslyn extraction pipeline is unchanged — only the on-disk format is new.

**Query speedup** (measured across 15 query types on real repos):

| Query | v1 (SQLite) | v2 (mmap) | Speedup |
|-------|------------|-----------|---------|
| `graph_trace_feature` | 13.2ms | 0.5ms | **26x** |
| `codemap_summarize` | 18.9ms | 0.9ms | **21x** |
| `surfaces_list_db_tables` | 5.7ms | 0.2ms | **28x** |
| `surfaces_list_config_keys` | 3.6ms | 0.2ms | **18x** |
| `types_hierarchy` | 8.7ms | 1.0ms | **9x** |
| `symbols_get_context` | 28.7ms | 5.3ms | **5x** |
| `symbols_get_card` | 7.8ms | 2.7ms | **3x** |

**Indexing speedup** (Roslyn compilation dominates, but I/O is faster):

| Repo | v1 | v2 | Speedup |
|------|-----|-----|---------|
| eShopOnWeb (278 files) | 16.2s | 5.8s | **2.8x** |
| Bitwarden (4,466 files) | ~170s | ~110s | **1.5x** |
| dotnet/roslyn (18,799 files) | 138.2s | 96.8s | **1.4x** |

**What changed:**
- Baselines stored as contiguous packed binary segments (symbols, edges, files, facts) with mmap reads — no SQL parsing overhead
- Custom search index with tokenized FTS (CamelCase splitting, signature/documentation indexing)
- WAL-backed overlay for workspace mutations (same isolation model)
- Zero native DLL dependencies (no `e_sqlite3.dll`)

**Validated on 9+ repos** including dotnet/roslyn (174K symbols, 768K references), dotnet/fsharp (157K symbols via FCS), and Bitwarden. Zero functional bugs. See [`docs/ENGINE-COMPARISON-RESULTS.MD`](docs/ENGINE-COMPARISON-RESULTS.MD) for full data.

---

## Self-Hosting Validated

CodeMap indexes its own 18-project solution (5,576 symbols, 20,960 references).
All 28 tools verified against real-world architectural complexity. Self-hosting exposed
and fixed cross-project reference bugs, CamelCase FTS edge cases, overlay StringId
resolution issues, and multi-line SQL extraction gaps. Every tool in this README was
tested against the codebase that implements it.

---

## Installation

### .NET Global Tool — NuGet (recommended)

See the **[Install via Claude Code or manually](#install-via-claude-code-or-manually)** section at the top for the one-paste Claude Code prompt and manual steps.

> **NuGet package:** [nuget.org/packages/codemap-mcp](https://www.nuget.org/packages/codemap-mcp)

### Docker

```bash
docker build -t codemap-mcp .
docker run -i \
  -v /path/to/your/repo:/repo:ro \
  -v /path/to/cache:/cache \
  codemap-mcp
```

> `-i` is required — MCP uses stdio transport. Without it the container gets immediate EOF.

Uses the .NET SDK base image (~800MB) because `MSBuildWorkspace` needs MSBuild at runtime for `index_ensure_baseline`. Mount a cache volume (`-v /path/to/cache:/cache`) to avoid rebuilding the index on every container start.

---

## Connect to Your AI Agent

### Claude Code (Claude Desktop / claude.ai)

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "codemap": {
      "command": "codemap-mcp"
    }
  }
}
```

### Any MCP-Compatible Client

CodeMap speaks standard MCP over **stdin/stdout** (JSON-RPC 2.0). Any MCP client works.

### CLAUDE.md Integration

Drop the instruction block from [`docs/CLAUDE-INSERT.MD`](docs/CLAUDE-INSERT.MD) into your project's `CLAUDE.md` to wire up automatic CodeMap usage for any Claude agent working on that project. The block includes the session startup sequence, a tool substitution decision table, and the "refresh before grep" rule that keeps agents in semantic mode.

---

## Tip: Write XML Docs — CodeMap Uses Them

CodeMap indexes `/// <summary>` XML doc comments on all classes, methods, and
interfaces. They appear in `symbols_get_card`, `symbols_get_context`, and
`symbols_search` results — giving agents intent and context without reading
implementations.

**When writing C# code with CodeMap enabled, always add XML doc comments.**
This isn't just style — it directly improves every downstream query. Agents
using `graph_trace_feature` see annotated call trees that read like specs.
`codemap_export` includes docs in the portable context for other LLMs.

See [`docs/CODEMAP-AGENT-GUIDE.MD`](docs/CODEMAP-AGENT-GUIDE.MD) for the full agent workflow guide.

---

## Architecture

```
Your Git repo              CodeMap Server
     │                          │
     │  repo_path               │
     ├─────────────────────────►│ GitService         (repo identity, HEAD SHA)
     │                          │      │
     │  solution.sln/.slnx      │      ▼
     ├─────────────────────────►│ RoslynCompiler     (MSBuildWorkspace for C#/VB, FCS for F#)
     │                          │      │
     │                          │      ▼
     │                          │ Extractors         (Symbols + Refs + TypeRelations + Facts)
     │                          │      │
     │                          │      ▼
     │                          │ CustomSymbolStore   (v2 binary segments, mmap'd)
     │                          │      │        ↕
     │                          │      │   SharedCache  (file-based, optional)
     │                          │      ▼
     │  your uncommitted edits  │      ▼
     ├─────────────────────────►│ OverlayStore        (WAL-backed incremental overlay)
     │                          │      │
     │                          │      ▼
     │                          │ MergedQueryEngine  (baseline + overlay, transparent merge)
     │                          │      │
     │  MCP tool call           │      ▼
     ├─────────────────────────►│ McpServer          (stdio JSON-RPC 2.0, 28 tools)
     │                          │      │
     │  JSON response           │      ▼
     │◄─────────────────────────│ ResponseEnvelope   (answer + evidence + timing + token savings)
```

**Layer dependencies** (enforced at build time — violations are build errors):

```
CodeMap.Core      ← zero dependencies (domain types + interfaces)
CodeMap.Git       ← Core (LibGit2Sharp)
CodeMap.Roslyn    ← Core (Roslyn 5.x + MSBuildWorkspace)
CodeMap.Storage.Engine ← Core (v2 binary segments, sole engine since v2.1.0)
CodeMap.Query     ← Core + Storage.Engine (query engine + cache + overlay merge)
CodeMap.Mcp       ← Core + Query (MCP tool handlers)
CodeMap.Daemon    ← ALL (DI composition root, the executable)
```

---

## Observability

Every response includes:
- **Per-phase timing** — `cache_lookup_ms`, `db_query_ms`, `ranking_ms` (sub-millisecond on v2)
- **Token savings** — tokens saved and cost avoided vs raw file reading
- **Semantic level** — `Full` / `Partial` / `SyntaxOnly` (index quality signal, see below)
- **Overlay revision** — which workspace revision answered the query
- **Workspace ID** — which workspace context answered (null for committed mode)

**When the level isn't `Full`**, `index_ensure_baseline`'s `project_diagnostics` says why, per project:
- `compiled: false`: the project couldn't be compiled, so it was indexed syntactically.
- `missing_restore_output`: the checkout was never restored (fresh clone or git worktree) and packages,
  sometimes the framework too, are unresolved. Run `dotnet restore`, then rebuild the baseline (L-12).
- `generator_load_failures`: a source generator from a newer .NET SDK couldn't load, so its code
  (e.g. Razor components) is missing (L-11).

The same level is reported on every later query against that baseline (for baselines built by
v2.8.2 or later; older ones don't record these fields).

Structured logs to `~/.codemap/logs/codemap-{date}.log` (daily rotation, JSON lines).
Cumulative savings to `~/.codemap/_savings.json` (persists across restarts).
Config at `~/.codemap/config.json`, read once at startup. It has two keys:

```json
{ "log_level": "Information", "shared_cache_dir": "/shared/codemap-cache" }
```

- `log_level`: `Trace`, `Debug`, `Information` (default), `Warning` or `Error`.
- `shared_cache_dir`: the shared baseline cache, used only when `CODEMAP_CACHE_DIR` is unset.

Any other key (a typo, or `budget_overrides`, which never had an effect and was removed in v2.10.0) is
ignored with one warning in the log.

### Data Directory (`CODEMAP_HOME`)

Everything above lives under one data root, `~/.codemap` by default. Set `CODEMAP_HOME` to move it
(config, logs, store, `_savings.json`), for example per test run, CI job or sandbox. It must be an
absolute path or start with `~/`; a relative value stops the server at startup (exit code 2).

Baselines are stored in `<data root>/store/<repoId>/baselines/<commitSha>/` as binary segment files.
Overlays are in `<data root>/store/<repoId>/overlays/<workspaceId>/`. Use `index_list_baselines` to inspect
and `index_cleanup` to reclaim space.

---

## Known Limitations & Coverage Gaps

CodeMap won't surface a hit in every situation a `grep` would. The most
common reasons are documented in [`docs/KNOWN-LIMITATIONS.md`](docs/KNOWN-LIMITATIONS.md).
Top items to be aware of:

- **Multi-target conditional symbols.** `#if NET8_0`-only types are
  invisible — extraction runs on the highest TFM only (L-01).
- **Legacy MVC `MapControllerRoute`** — convention-routed actions don't
  surface in `surfaces_list_endpoints`. Only attribute routing,
  minimal API, and Blazor `@page` are extracted (L-02).
- **F# fact extractors not yet wired** — F# gets symbols/refs/hierarchy
  only; endpoints / DI / config / DB tables don't extract from `.fsproj`
  yet (L-05).
- **Unrestored checkout** — a fresh clone or git worktree has no
  `obj/project.assets.json`, and CodeMap doesn't restore, so package references
  (sometimes the framework itself) are unresolved. It's reported
  (`missing_restore_output`, `semantic_level: partial`); run `dotnet restore`
  before `index_ensure_baseline` (L-12).
- **.NET SDK newer than CodeMap's Roslyn** — that SDK's source generators
  (e.g. Razor) may not load, and their output is missing. Reported as
  `generator_load_failures`; update CodeMap (L-11).
- **Fresh clone with no build** — Razor source-generator output may be
  invisible until you `dotnet build` once (L-08).
- **Razor markup-only uses** — `@onclick="Save"` and a component tag
  like `<Greeting />` produce no reference yet, and Razor references point
  at the generated `obj/…_razor.g.cs` lines, not the `.razor` file (L-15).

When `symbols_search` returns nothing for code you can see in the
editor, scan KNOWN-LIMITATIONS first before falling back to grep.

---

## Documentation

| Doc | What's in it |
|-----|-------------|
| [`docs/CLAUDE-INSERT.MD`](docs/CLAUDE-INSERT.MD) | Copy-paste block for CLAUDE.md — wires up agent to use CodeMap |
| [`docs/CODEMAP-AGENT-GUIDE.MD`](docs/CODEMAP-AGENT-GUIDE.MD) | Full agent operating guide: startup, refresh, query patterns, common mistakes |
| [`docs/KNOWN-LIMITATIONS.md`](docs/KNOWN-LIMITATIONS.md) | Coverage gaps and intentional non-features — what `grep` finds that CodeMap doesn't |
| [`docs/DEVELOPER-GUIDE.MD`](docs/DEVELOPER-GUIDE.MD) | How to add tools, extractors, storage methods |
| [`docs/ARCHITECTURE-WALKTHROUGH.MD`](docs/ARCHITECTURE-WALKTHROUGH.MD) | Request traces, data model, decision log |
| [`docs/API-SCHEMA.MD`](docs/API-SCHEMA.MD) | Every type definition and MCP tool contract |
| [`docs/SYSTEM-ARCHITECTURE.MD`](docs/SYSTEM-ARCHITECTURE.MD) | Component design, DB schema, query model |

---

## Build & Test

```bash
# Build (zero warnings enforced)
dotnet build -warnaserror

# Test per project: `dotnet test` at the solution root starts every test runner at once and can run out of memory
dotnet run --project tests/CodeMap.Core.Tests          # likewise Roslyn, Query, Mcp, Storage.Engine, Daemon, Git

# Integration tests: restore the test fixtures once per checkout first (MSBuildWorkspace doesn't restore)
dotnet restore testdata/SampleSolution/SampleSolution.sln   # likewise SampleVbSolution, SampleBlazorSolution, SampleFSharpSolution
dotnet run --project tests/CodeMap.Integration.Tests   # includes Category=Concurrency (spawns real servers, ~1 min)

# Linux test rig (Docker; see docs/DEVELOPER-GUIDE.MD)
.\tests\docker\run-linux-tests.ps1 integration

# Token savings benchmark
dotnet test --filter "Category=Benchmark" -v normal

# Performance microbenchmarks (BenchmarkDotNet)
cd tests/CodeMap.Benchmarks && dotnet run -c Release
```

---

## Performance Reference

What to expect when running CodeMap on your codebase. All v2 engine numbers (default since v2.0.0).

### Indexing time by repo size

| Repo | Files | Symbols | Refs | Index time |
|------|-------|---------|------|-----------|
| CodeMap (self-hosted) | 585 | 6,800 | 29,200 | ~24s |
| eShopOnWeb | 278 | — | — | ~6s |
| dotnet/fsharp | 994 | 157,000 | 58,000 | ~131s |
| Bitwarden | 4,466 | — | — | ~110s |
| dotnet/roslyn | 18,799 | 174,000 | 768,000 | ~97s |

Subsequent runs on the same commit return immediately (`already_existed: true`). Incremental overlay refresh (after editing files) takes **~63ms**.

### Query response time (v2 engine)

| Query | Cold (first hit, no L1 cache) | Warm (L1 cache) |
|-------|------------------------------|-----------------|
| `symbols_search` | 1–10ms | <1ms |
| `symbols_get_card` | 2–10ms | <1ms |
| `symbols_get_context` | 5–30ms | 1–5ms |
| `refs_find` | 5–20ms | <1ms |
| `graph_callers / callees` | 10–50ms | 1–5ms |
| `graph_trace_feature` | 10–100ms | 1–10ms |
| `types_hierarchy` | 1–5ms | <1ms |
| `codemap_summarize` | 50–200ms | 5–20ms |
| `surfaces.list_*` | 1–10ms | <1ms |
| `index_diff` | 100–500ms | — |

Cold times scale with repo size (more symbols = more BFS/join work). Warm times are nearly flat across all repo sizes — L1 cache caps at 10,000 entries with LRU eviction.

### Memory footprint (v2 engine)

| Repo size | Baseline on disk | Resident memory (mmap) |
|-----------|-----------------|----------------------|
| Small (<1K symbols) | ~1–5 MB | ~5–20 MB |
| Medium (10K symbols) | ~20–50 MB | ~30–80 MB |
| Large (100K+ symbols) | ~200–500 MB | ~300–600 MB |

mmap pages are demand-loaded by the OS — resident memory stays proportional to queries made, not total index size.

### Process memory while idle (v2.10.0+)

The server process also holds what Roslyn used to build the index. Since v2.10.0 it gives that memory back
when idle: one compacting GC two minutes after indexing, and the cached solution for overlay refreshes is
dropped after 10 minutes without a refresh. Private memory of one idle server, measured
(PHASE-21-12, `docs/benchmarks/phase-21-12/`):

| Repo | v2.9.0, idle | v2.10.0, 2.5 min idle | v2.10.0, 13 min idle |
|---|---:|---:|---:|
| eShopOnWeb | 363 MB | 218 MB | — |
| Bitwarden server | 723 MB | 342 MB | 352 MB |
| nopCommerce | 2,922 MB | 1,116 MB | 204 MB |

Each server logs `MEMORY_SNAPSHOT` lines (working set, private bytes, GC heap) to `~/.codemap/logs`.

---

*28 MCP tools. 90%+ token savings. Roslyn-grade semantics. C#, VB.NET, F#, **Blazor/Razor**. DLL boundary navigation. `.sln` + `.slnx` auto-discovery. v2.10.0 — safe destructive tools across agents, idle memory returned, Razor component and view references, honest `config.json`. Validated on dotnet/roslyn (174K symbols), dotnet/fsharp (157K symbols), and a 9-repo Blazor corpus including Blazorise, MudBlazor, ant-design-blazor, OrchardCore. Your agent deserves better than grep.*
