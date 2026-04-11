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

### Upgrading from v1.x

v2.0.0 uses a new binary storage engine (memory-mapped segments instead of SQLite). Your old `.db` baselines are not auto-migrated — run `index.ensure_baseline` once per repo to rebuild. Old baselines are harmless and can be cleaned with `index.cleanup`. If you need the old engine temporarily, set `CODEMAP_ENGINE=sqlite`.

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
refs.find { symbol_id: "M:MyApp.Services.OrderService.SubmitAsync", kind: "Call" }
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

---

## The Transformation

Here's what changes when you give an agent CodeMap:

| Without CodeMap | With CodeMap |
|---|---|
| `grep -rn "OrderService" src/` | `symbols.search { query: "OrderService" }` |
| Read 5 files to understand a method | `symbols.get_context` — card + source + all callees in one call |
| Manually trace call chains across files | `graph.trace_feature` — full annotated tree, one call |
| Hope grep finds the right interface impl | `types.hierarchy` — base, interfaces, derived types, instant |
| Read the whole file to find config usage | `surfaces.list_config_keys` — every `IConfiguration` access, indexed |
| Diff two commits by reading changed files | `index.diff` — semantic diff, rename-aware, architectural changes only |

The agent stops **reading** your codebase and starts **understanding** it.

---

## Showpiece: `graph.trace_feature`

The most powerful tool. Replaces 5–10 manual calls with one:

```json
graph.trace_feature {
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
| Find all callers (refs.find) | 3,609 | 220 | **94%** |
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
| `symbols.search` | FTS search by name, kind, namespace, or file path |
| `code.search_text` | Regex/substring search across source files — returns file:line:excerpt |
| `symbols.get_card` | Full symbol metadata + architectural facts + source code |
| `symbols.get_context` | Card + source + all callees with source — deep understanding in one call |
| `symbols.get_definition_span` | Raw source only, no overhead |
| `code.get_span` | Read any source excerpt by line range |

### Navigate
| Tool | What it does |
|------|-------------|
| `refs.find` | All references to a symbol, classified (Call, Read, Write, Implementation…) |
| `graph.callers` | Depth-limited caller graph — who triggers this? |
| `graph.callees` | Depth-limited callee graph — what does this orchestrate? |
| `graph.trace_feature` | Full annotated feature flow with facts at every node |
| `types.hierarchy` | Base type, interfaces implemented, and all derived types |

### Architecture
| Tool | What it does |
|------|-------------|
| `codemap.summarize` | Full codebase overview: endpoints, DI, config, DB, middleware, logging |
| `codemap.export` | Portable context dump (markdown/JSON, 3 detail levels) for any LLM |
| `codemap.guide` | Quick-start guide: session setup, decision table, and usage rules for agents |
| `index.diff` | Semantic diff between commits: symbols added/removed/renamed, API changes |
| `surfaces.list_endpoints` | Every HTTP route (controller + minimal API) with handler and file:line |
| `surfaces.list_config_keys` | Every `IConfiguration` access with usage pattern |
| `surfaces.list_db_tables` | EF Core entities + `[Table]` attributes + raw SQL table references |

### Workspace
| Tool | What it does |
|------|-------------|
| `workspace.create` | Isolated overlay for in-progress edits |
| `workspace.reset` | Clear overlay, back to baseline |
| `workspace.list` | All active workspaces with staleness, SemanticLevel, and fact count |
| `workspace.delete` | Remove a workspace |
| `index.refresh_overlay` | Re-index changed files incrementally (~63ms) |

### Index Management
| Tool | What it does |
|------|-------------|
| `index.ensure_baseline` | Build the semantic index (idempotent, cache-aware, auto-discovers solution) |
| `index.list_baselines` | All cached baselines with size, age, and commit |
| `index.cleanup` | Remove stale baselines (dry-run default) |
| `index.remove_repo` | Remove ALL baselines for a repo (ignores protection rules) |

### Repo
| Tool | What it does |
|------|-------------|
| `repo.status` | Git state + whether a baseline exists for current HEAD |

---

## Workspace Mode — See Your Own Edits

CodeMap tracks uncommitted changes via an **overlay index**. Every agent session gets its own isolated workspace:

```
1. index.ensure_baseline   → index HEAD once
2. workspace.create        → agent gets isolated overlay
3. Edit files on disk
4. index.refresh_overlay   → re-indexes only changed files (~63ms)
5. Query with workspace_id → results include your in-progress code
```

Three consistency modes:
- **Committed** — baseline index only (default, no workspace needed)
- **Workspace** — baseline + your uncommitted edits merged
- **Ephemeral** — workspace + virtual file contents (unsaved buffer content)

---

## Multi-Agent Supervisor Support

Running multiple agents in parallel? CodeMap has you covered:

- Each agent gets its own isolated workspace — no cross-contamination
- `workspace.list` shows every workspace: `IsStale`, `SemanticLevel`, fact count
- Stale detection fires when a workspace's base commit diverges from HEAD
- Supervisor can inspect, clean up, or re-provision any agent's workspace

---

## Self-Healing Under Broken Builds

When a file doesn't compile, CodeMap doesn't drop references. It stores **unresolved edges** with syntactic hints. When compilation succeeds again (after a fix), a **resolution worker** automatically upgrades them to fully-resolved semantic edges.

`refs.find` returns both. Filter with `resolution_state: "resolved"` if you need certainty.

---

## DLL Boundary Navigation

CodeMap resolves DLL symbols lazily on first agent access — `NOT_FOUND` at a DLL
boundary triggers automatic extraction rather than a dead end.

**Two levels, both permanent (cached in baseline DB):**

| Level | Trigger | What you get | Cost |
|-------|---------|--------------|------|
| 1 — Metadata stub | Any `NOT_FOUND` query | Method signatures, XML docs, type hierarchy | ~1–5ms (once) |
| 2 — Decompiled source | `symbols.get_card` with `include_code: true` | Full reconstructed C# source via ICSharpCode.Decompiler | ~10–200ms (once) |

After Level 2, cross-DLL call graph edges are extracted so `graph.callees` and
`graph.trace_feature` traverse INTO and THROUGH DLL code seamlessly.

**`source` discriminator in `symbols.get_card` response:**
- `"source_code"` — symbol is from your own source
- `"metadata_stub"` — Level 1 only (decompilation unavailable)
- `"decompiled"` — Level 2 source reconstructed and ready

`graph.trace_feature` applies a `max_lazy_resolutions_per_query` budget (default 20)
when encountering previously-unseen DLL types to bound decompilation latency.

---

## Shared Baseline Cache

Index once, reuse everywhere — across machines, CI, Docker containers:

```bash
export CODEMAP_CACHE_DIR=/shared/codemap-cache
```

- `index.ensure_baseline` pulls from cache first (~2ms vs ~30s Roslyn build)
- Auto-push after every new baseline build
- Self-healing: corrupt cache entries are detected and overwritten
- Zero config when `CODEMAP_CACHE_DIR` is unset — all cache ops are no-ops

---

## v2 Storage Engine — 10x Faster Queries

v2.0.0 replaces SQLite with a **custom binary storage engine** using memory-mapped segment files. The Roslyn extraction pipeline is unchanged — only the on-disk format is new.

**Query speedup** (measured across 15 query types on real repos):

| Query | v1 (SQLite) | v2 (mmap) | Speedup |
|-------|------------|-----------|---------|
| `graph.trace_feature` | 13.2ms | 0.5ms | **26x** |
| `codemap.summarize` | 18.9ms | 0.9ms | **21x** |
| `surfaces.list_db_tables` | 5.7ms | 0.2ms | **28x** |
| `surfaces.list_config_keys` | 3.6ms | 0.2ms | **18x** |
| `types.hierarchy` | 8.7ms | 1.0ms | **9x** |
| `symbols.get_context` | 28.7ms | 5.3ms | **5x** |
| `symbols.get_card` | 7.8ms | 2.7ms | **3x** |

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

Uses the .NET SDK base image (~800MB) because `MSBuildWorkspace` needs MSBuild at runtime for `index.ensure_baseline`. Mount a cache volume (`-v /path/to/cache:/cache`) to avoid rebuilding the index on every container start.

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
interfaces. They appear in `symbols.get_card`, `symbols.get_context`, and
`symbols.search` results — giving agents intent and context without reading
implementations.

**When writing C# code with CodeMap enabled, always add XML doc comments.**
This isn't just style — it directly improves every downstream query. Agents
using `graph.trace_feature` see annotated call trees that read like specs.
`codemap.export` includes docs in the portable context for other LLMs.

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
- **Semantic level** — `Full` / `Partial` / `SyntaxOnly` (index quality signal)
- **Overlay revision** — which workspace revision answered the query
- **Workspace ID** — which workspace context answered (null for committed mode)

Structured logs to `~/.codemap/logs/codemap-{date}.log` (daily rotation, JSON lines).
Cumulative savings to `~/.codemap/_savings.json` (persists across restarts).
Config at `~/.codemap/config.json` (log level, cache dir, budget overrides).

### v2 Data Directory

Baselines are stored in `~/.codemap/store/<repoId>/baselines/<commitSha>/` as binary segment files. Overlays in `~/.codemap/store/overlays/<workspaceId>/`. Use `index.list_baselines` to inspect and `index.cleanup` to reclaim space.

---

## Documentation

| Doc | What's in it |
|-----|-------------|
| [`docs/CLAUDE-INSERT.MD`](docs/CLAUDE-INSERT.MD) | Copy-paste block for CLAUDE.md — wires up agent to use CodeMap |
| [`docs/CODEMAP-AGENT-GUIDE.MD`](docs/CODEMAP-AGENT-GUIDE.MD) | Full agent operating guide: startup, refresh, query patterns, common mistakes |
| [`docs/DEVELOPER-GUIDE.MD`](docs/DEVELOPER-GUIDE.MD) | How to add tools, extractors, storage methods |
| [`docs/ARCHITECTURE-WALKTHROUGH.MD`](docs/ARCHITECTURE-WALKTHROUGH.MD) | Request traces, data model, decision log |
| [`docs/API-SCHEMA.MD`](docs/API-SCHEMA.MD) | Every type definition and MCP tool contract |
| [`docs/SYSTEM-ARCHITECTURE.MD`](docs/SYSTEM-ARCHITECTURE.MD) | Component design, DB schema, query model |

---

## Build & Test

```bash
# Build (zero warnings enforced)
dotnet build -warnaserror

# Fast unit tests
dotnet test --filter "Category!=Integration&Category!=Benchmark"

# Integration tests (requires MSBuild)
dotnet test --filter "Category=Integration"

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
| `symbols.search` | 1–10ms | <1ms |
| `symbols.get_card` | 2–10ms | <1ms |
| `symbols.get_context` | 5–30ms | 1–5ms |
| `refs.find` | 5–20ms | <1ms |
| `graph.callers / callees` | 10–50ms | 1–5ms |
| `graph.trace_feature` | 10–100ms | 1–10ms |
| `types.hierarchy` | 1–5ms | <1ms |
| `codemap.summarize` | 50–200ms | 5–20ms |
| `surfaces.list_*` | 1–10ms | <1ms |
| `index.diff` | 100–500ms | — |

Cold times scale with repo size (more symbols = more BFS/join work). Warm times are nearly flat across all repo sizes — L1 cache caps at 10,000 entries with LRU eviction.

### Memory footprint (v2 engine)

| Repo size | Baseline on disk | Resident memory (mmap) |
|-----------|-----------------|----------------------|
| Small (<1K symbols) | ~1–5 MB | ~5–20 MB |
| Medium (10K symbols) | ~20–50 MB | ~30–80 MB |
| Large (100K+ symbols) | ~200–500 MB | ~300–600 MB |

mmap pages are demand-loaded by the OS — resident memory stays proportional to queries made, not total index size.

---

*28 MCP tools. 90%+ token savings. Roslyn-grade semantics. C#, VB.NET, and F# — all three .NET languages. DLL boundary navigation. `.sln` + `.slnx` auto-discovery. v2.3.0 — solution auto-discovery, short SHA expansion, production bug fixes. Validated on dotnet/roslyn (174K symbols) and dotnet/fsharp (157K symbols). Your agent deserves better than grep.*
