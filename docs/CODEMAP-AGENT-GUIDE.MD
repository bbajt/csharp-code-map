# CodeMap MCP — Agent Operating Guide

This document is for AI agents (Claude Code, etc.) using CodeMap MCP tools.
Include it in your system prompt or CLAUDE.md for the target project.

---

## Session Startup (Do This First, Every Session)

```
1. index.ensure_baseline { repo_path: "." }
   → Builds or reuses the semantic index for HEAD commit.
   → Fast if already indexed (~1ms cache hit).

2. workspace.create { repo_path: ".", workspace_id: "session" }
   → Creates an isolated overlay for tracking your changes.
   → All subsequent queries should include workspace_id: "session".
```

This takes <2 seconds total. Do it before any code exploration.

---

## During Active Development (Critical Workflow)

**After EVERY code change** (adding a method, editing a file, etc.):

```
index.refresh_overlay { repo_path: ".", workspace_id: "session" }
```

This takes **~63ms** — essentially free. It re-indexes only the changed
files and updates your workspace overlay. After refreshing:

- `symbols.search` finds your new methods
- `symbols.get_card` returns updated signatures
- `refs.find` shows references to/from your new code
- `graph.callers/callees` includes your new call chains
- `graph.trace_feature` traces through your new code

**Do NOT skip this step.** Without it, CodeMap sees the old baseline
and won't find any code you've added or modified this session.

**Do NOT fall back to Grep/Read** just because a symbol isn't found.
If a search returns nothing for code you just wrote, refresh the overlay
first — it almost certainly fixes the issue.

---

## Query Pattern (Always Include workspace_id)

Every query during active development should include `workspace_id`:

```
symbols.search { repo_path: ".", query: "MyNewMethod", workspace_id: "session" }
symbols.get_card { repo_path: ".", symbol_id: "...", workspace_id: "session" }
symbols.get_context { repo_path: ".", symbol_id: "...", workspace_id: "session" }
refs.find { repo_path: ".", symbol_id: "...", workspace_id: "session" }
graph.callers { repo_path: ".", symbol_id: "...", workspace_id: "session" }
graph.trace_feature { repo_path: ".", entry_point: "...", workspace_id: "session" }
```

Without `workspace_id`, queries only see the committed baseline — your
in-progress edits are invisible.

---

## Session End

```
workspace.delete { repo_path: ".", workspace_id: "session" }
```

Or just leave it — stale workspaces are cleaned up by `index.cleanup`.

If you committed your changes during the session:

```
index.ensure_baseline { repo_path: "." }
```

This builds a new baseline for the new commit. The next session starts
with fresh indexed data.

---

## When to Use Each Tool

### Understanding code (most common)

```
symbols.get_context   → Card + source code + callee cards with code
                        Use this as your PRIMARY exploration tool.
                        One call = full understanding of a method.

symbols.get_card      → Card + source code (no callees)
                        Use when you only need one symbol's details.

symbols.search        → Find symbols by name OR browse by kind (v1.3.1+)

                        Mode 1 — text search (provide query):
                          symbols.search { query: "OrderService" }
                          symbols.search { query: "Order*" }   ← prefix

                        Mode 2 — kind browse (omit query):
                          symbols.search { kinds: ["Class"] }
                          symbols.search { kinds: ["Interface","Enum"] }
                          symbols.search { kinds: ["Class"], namespace: "MyApp" }
                          → Returns all symbols of those types, up to limit.
                          → query:"*" is also accepted and treated as kind browse.

                        Always copy symbol_id from results.
                        NEVER type FQNs manually.

code.search_text      → Regex/substring search across source file content
                        Use when you know WHAT text to find but not WHERE.
                        "Find all files containing 'OrderNotFoundException'"
                        "Find all TODO comments in src/"
                        Supports file_path filter and limit (default 50, max 200).
```

### Navigating relationships

```
refs.find             → Who uses this symbol? (classified: Call, Read, Write)
graph.callers         → Call chain upward (who triggers this?)
graph.callees         → Call chain downward (what does this call?)
graph.trace_feature   → Full annotated feature flow (the power tool)
types.hierarchy       → Inheritance: base type, interfaces, derived types
```

### Architecture overview

```
codemap.summarize     → Full codebase overview (endpoints, DI, config, etc.)
codemap.export        → Portable context for pasting into other LLMs
index.diff            → What changed between two commits (semantic, not text)
```

### Surface queries

```
surfaces.list_endpoints    → All HTTP endpoints
surfaces.list_config_keys  → All configuration key usage
surfaces.list_db_tables    → All database tables (EF + raw SQL)
```

---

## Common Mistakes

### 1. Forgetting to refresh after edits
**Symptom:** Search returns nothing for code you just wrote.
**Fix:** `index.refresh_overlay { workspace_id: "session" }`

### 2. Querying without workspace_id
**Symptom:** Can't find in-progress changes; only sees committed code.
**Fix:** Add `workspace_id: "session"` to every query.

### 3. Typing FQNs manually
**Symptom:** NOT_FOUND for a symbol you know exists.
**Fix:** Use `symbols.search("MethodName")` first, then copy the
`symbol_id` from the result. Roslyn doc-comment IDs require exact
format: `T:Namespace.Class`, `M:Namespace.Class.Method(ParamType)`.

### 4. Multi-term FTS search returning nothing
**Symptom:** `symbols.search("Foo Bar Baz")` returns zero results.
**Fix:** FTS5 treats spaces as AND. Search one term at a time, or use
`OR` between terms: `"Foo OR Bar OR Baz"`.

### 5. Using `"*"` to list all symbols of a kind
**Symptom:** `symbols.search { query: "*", kinds: ["Class"] }` returns
an error (`-32603 Internal error` on older builds, `INVALID_ARGUMENT`
on newer ones).
**Why:** FTS5 does not accept a bare `*` as a match-all wildcard.
**Fix (v1.3.1+):** Omit `query` entirely and pass the `kinds` filter:
```
symbols.search { repo_path: ".", kinds: ["Class"], workspace_id: "session" }
```
Or pass `query: "*"` — v1.3.1+ silently treats it as no query and routes
to the kind-browse path automatically.
Both forms return all symbols of the requested kinds up to `limit` (default 20, max 100).

### 5. Using Bash grep for source file searches
**Symptom:** Running `grep -rn "pattern" src/` when CodeMap is available.
**Better:** Use `code.search_text { pattern: "...", file_path: "src/" }`.
Returns file:line:excerpt for each match, restricted to indexed source files.
Supports regex. Default limit 50, max 200.
**When Bash grep is still right:** Non-C# files (YAML, JSON, `.md`, `.csproj`),
or files outside the project index (bin/, obj/, generated/).

---

## Refresh Frequency Guide

| Situation | Action |
|-----------|--------|
| Session start | `index.ensure_baseline` + `workspace.create` |
| After editing 1-5 files | `index.refresh_overlay` (63ms) |
| After a large refactor (20+ files) | `index.refresh_overlay` (still fast, ~100-200ms) |
| After committing | `index.ensure_baseline` (new baseline for new commit) |
| After pulling / switching branches | `index.ensure_baseline` (builds or reuses baseline for new HEAD) |
| Before a code review query | `index.refresh_overlay` to ensure latest state |

The overlay refresh is cheap enough to call after every edit batch.
When in doubt, refresh.

---

## XML Doc Comments — Write Them, CodeMap Uses Them

CodeMap indexes `/// <summary>` XML doc comments and surfaces them in
`symbols.get_card`, `symbols.get_context`, and `symbols.search` results.

**When writing code, always add XML doc comments** to every public and
internal class, method, interface, and property. This is not just style —
it directly improves the quality of every CodeMap query that touches
that symbol. Without XML docs, agents see only the raw signature.

```csharp
/// <summary>
/// Validates the order and submits it to the payment gateway.
/// Returns a tracking ID on success, or an error if validation fails.
/// </summary>
public async Task<Result<string, OrderError>> SubmitOrderAsync(Order order, CancellationToken ct)
```

**Why this matters:**
- `symbols.get_card` shows the doc as `DocumentationSnippet` — agents
  understand intent without reading the implementation
- `symbols.search` ranks documented symbols higher — better search results
- `graph.trace_feature` annotates each node with its doc — the feature
  trace reads like a specification, not just a call chain
- `codemap.export` includes docs in the portable context — other LLMs
  benefit too

---

## What CodeMap Knows vs What It Doesn't

### CodeMap knows (use it for these):
- Every symbol in your solution (classes, methods, properties, etc.)
- XML doc comments (`/// <summary>`) — indexed and shown in card/context results
- Call relationships (who calls whom)
- Type hierarchy (inheritance, interfaces)
- Architectural facts (endpoints, config keys, DB tables, DI, etc.)
- Source code of any indexed symbol

### CodeMap doesn't know (use Grep/Read for these):
- BCL/NuGet types (only your solution's code is indexed)
- Inline comments and string literals — use `code.search_text` for these
  (returns file:line:excerpt for regex/substring matches across source files)
- Non-C# files: YAML, JSON, Markdown, `.csproj`, build scripts
- Runtime behavior (what actually executes at runtime)

Note: XML doc comments (`/// <summary>`) ARE indexed and searchable.
Inline `//` comments are not indexed as metadata but ARE found by `code.search_text`.

---

*Include this file as a reference in your project's CLAUDE.md or
system prompt to help AI agents use CodeMap effectively.*
