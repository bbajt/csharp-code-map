# CodeMap — Turn Your AI Agent Into a Semantic Dragon

> *Stop feeding your AI agent raw source files. Give it a semantic index instead.*

CodeMap is a **Roslyn-powered MCP server** that lets AI agents navigate C# codebases by **symbol, call graph, and architectural fact** — instead of brute-reading thousands of lines of source code. One tool call. Precise answer. No context flood.

**Average token savings: 90%+** versus reading files directly.

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

CodeMap builds a **persistent semantic index** from your `.sln` file using Roslyn — the same compiler that powers Visual Studio. The index captures:

- Every symbol (classes, methods, properties, interfaces, records)
- Every call relationship and reference (who calls what, where)
- Type hierarchy (inheritance chains, interface implementations)
- Architectural facts extracted from code: HTTP endpoints, config keys, DB tables, DI registrations, middleware pipeline, retry policies, exception throw points, structured log templates

All of this is exposed via **26 MCP tools** that any MCP-compatible AI agent can call.

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

## 26 Tools Across Six Categories

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
| `index.ensure_baseline` | Build the semantic index (idempotent, cache-aware) |
| `index.list_baselines` | All cached baselines with size, age, and commit |
| `index.cleanup` | Remove stale baselines (dry-run default) |

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

## Self-Hosting Validated

CodeMap indexes its own 17-project solution (4,932 symbols, 22,808 references).
All 26 tools verified against real-world architectural complexity. Self-hosting exposed
and fixed cross-project reference bugs, CamelCase FTS edge cases, and multi-line SQL
extraction gaps. Every tool in this README was tested against the codebase that implements it.

---

## Installation

### .NET Global Tool (recommended)

```bash
dotnet tool install -g codemap-mcp
codemap-mcp --version
```

### Self-Contained Binary (no .NET required)

```bash
# Linux/macOS
./scripts/build-release.sh 1.0.0

# Windows
.\scripts\build-release.ps1 1.0.0
```

Produces single-file executables for `win-x64`, `linux-x64`, and `osx-arm64`.
No .NET runtime required. No trimming (Roslyn uses reflection at runtime).

### Docker

```bash
docker build -t codemap-mcp .
docker run -i \
  -v /path/to/your/repo:/repo:ro \
  -v /path/to/cache:/cache \
  codemap-mcp
```

> `-i` is required — MCP uses stdio transport. Without it the container gets immediate EOF.

Note: Uses the .NET SDK base image (~800MB) because `MSBuildWorkspace` needs MSBuild at runtime for `index.ensure_baseline`. The shared cache (`-v /cache`) avoids rebuilding the index on every container start.

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

## Architecture

```
Your Git repo              CodeMap Server
     │                          │
     │  repo_path               │
     ├─────────────────────────►│ GitService         (repo identity, HEAD SHA)
     │                          │      │
     │  solution.sln            │      ▼
     ├─────────────────────────►│ RoslynCompiler     (MSBuildWorkspace → full semantic model)
     │                          │      │
     │                          │      ▼
     │                          │ Extractors         (Symbols + Refs + TypeRelations + Facts)
     │                          │      │
     │                          │      ▼
     │                          │ BaselineStore      (SQLite WAL + FTS5, one DB per commit)
     │                          │      │        ↕
     │                          │      │   SharedCache  (file-based, optional)
     │                          │      ▼
     │  your uncommitted edits  │      ▼
     ├─────────────────────────►│ OverlayStore       (SQLite per workspace, incremental)
     │                          │      │
     │                          │      ▼
     │                          │ MergedQueryEngine  (baseline + overlay, transparent merge)
     │                          │      │
     │  MCP tool call           │      ▼
     ├─────────────────────────►│ McpServer          (stdio JSON-RPC 2.0, 26 tools)
     │                          │      │
     │  JSON response           │      ▼
     │◄─────────────────────────│ ResponseEnvelope   (answer + evidence + timing + token savings)
```

**Layer dependencies** (enforced at build time — violations are build errors):

```
CodeMap.Core      ← zero dependencies (domain types + interfaces)
CodeMap.Git       ← Core (LibGit2Sharp)
CodeMap.Roslyn    ← Core (Roslyn 4.x + MSBuildWorkspace)
CodeMap.Storage   ← Core (SQLite + WAL + FTS5)
CodeMap.Query     ← Core + Storage (query engine + cache + overlay merge)
CodeMap.Mcp       ← Core + Query (MCP tool handlers)
CodeMap.Daemon    ← ALL (DI composition root, the executable)
```

---

## Observability

Every response includes:
- **Per-phase timing** — `cache_lookup_ms`, `db_query_ms`, `ranking_ms`
- **Token savings** — tokens saved and cost avoided vs raw file reading
- **Semantic level** — `Full` / `Partial` / `SyntaxOnly` (index quality signal)
- **Overlay revision** — which workspace revision answered the query

Structured logs to `~/.codemap/logs/codemap-{date}.log` (daily rotation, JSON lines).
Cumulative savings to `~/.codemap/_savings.json` (persists across restarts).
Config at `~/.codemap/config.json` (log level, cache dir, budget overrides).

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

*26 MCP tools. 90%+ token savings. Roslyn-grade semantics. Your agent deserves better than grep.*
