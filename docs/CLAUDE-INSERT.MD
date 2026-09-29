## Code Navigation — CodeMap MCP

**This project has CodeMap MCP enabled. If the codebase is C#/.NET:**
**STOP before reaching for `grep`, `cat`, `find`, `Get-ChildItem`, or any bash/shell
file search. CodeMap tools return precise semantic results without flooding context.**

---

### Session Start — Required Before Any Code Work

```
index_ensure_baseline  { repo_path: "." }
workspace_create       { repo_path: ".", workspace_id: "session" }
```

Fast (<2s total). Do this before reading any code or planning any changes.

Tool names use underscores (`symbols_search`; CodeMap v2.9.0+). Your client may show them with a
prefix, e.g. `mcp__codemap__symbols_search` in Claude Code.

**Fresh clone or git worktree?** Run `dotnet restore <solution>` before `index_ensure_baseline`.
CodeMap doesn't restore, and an unrestored checkout is indexed with unresolved packages.

**`semantic_level: partial` in a response** means some projects are indexed incompletely: missing
results there are not proof that code doesn't exist. `project_diagnostics` in the
`index_ensure_baseline` result says why (`missing_restore_output`: the checkout wasn't restored;
`generator_load_failures`: an SDK source generator didn't load). Tell the user rather than working
around it.

**`WORKSPACE_IN_USE`** means another CodeMap process (e.g. a parallel agent) holds that workspace.
Retry, or give each agent its own `workspace_id` (e.g. `"session-reviewer"`).

---

### Decision Table — What to Use Instead

| You want to... | Use this — NOT bash |
|---|---|
| Find a class, method, interface by name | `symbols_search { query: "Name" }` |
| Understand what a method does + calls | `symbols_get_context { symbol_id: "..." }` |
| Read a method's source code | `symbols_get_card { symbol_id: "..." }` |
| Find who calls a method | `graph_callers { symbol_id: "..." }` |
| Find what a method calls | `graph_callees { symbol_id: "..." }` |
| Trace a feature end-to-end | `graph_trace_feature { entry_point: "..." }` |
| Find all usages of a symbol | `refs_find { symbol_id: "..." }` |
| Check what a class implements/inherits | `types_hierarchy { symbol_id: "..." }` |
| Search for text / string literals in source | `code_search_text { pattern: "...", repo_path: "." }` |
| Understand overall architecture | `codemap_summarize { repo_path: "." }` |
| See all HTTP endpoints | `surfaces_list_endpoints { repo_path: "." }` |
| See all config key usage | `surfaces_list_config_keys { repo_path: "." }` |
| See all DB tables | `surfaces_list_db_tables` |
| See DI registrations | `codemap_summarize` (DI section; no dedicated tool yet) |

**Always include `workspace_id: "session"` in every query.**
Without it, queries only see committed code — in-progress edits are invisible.

---

### XML Doc Comments — Always Write Them

CodeMap indexes `/// <summary>` XML doc comments on all classes, methods,
interfaces, and properties. These appear in `symbols_get_card`,
`symbols_get_context`, and `symbols_search` results.

**Always add XML docs** to every public/internal class and method you create.
Good XML docs dramatically improve the context quality for every downstream
CodeMap query. Without them, agents see only the signature — no intent.

```csharp
/// <summary>
/// Validates the order and submits it to the payment gateway.
/// Returns a tracking ID on success, or an error if validation fails.
/// </summary>
public async Task<Result<string, OrderError>> SubmitOrderAsync(Order order, CancellationToken ct)
```

---

### Grep and Read Are Still Correct For

- Non-C# files: `.json`, `.yaml`, `.md`, `.csproj`, `.props`, build scripts
- Comments, TODOs, or prose inside documentation files
- Generated files outside the indexed source tree (`bin/`, `obj/`)
- `code_search_text` covers C# source text — use it before falling back to grep

---

### NEVER Type FQNs Manually

Roslyn doc-comment IDs are exact: `M:Namespace.Class.Method(ParamType)`.
A single wrong character returns NOT_FOUND.

**Always:** `symbols_search("MethodName")` → copy `symbol_id` from result.
**Never:** type `M:My.Namespace.Foo.Bar(System.String)` from memory.

---

### After Every File Edit — Refresh the Overlay

```
index_refresh_overlay { repo_path: ".", workspace_id: "session" }
```

Takes ~63ms. Keeps CodeMap in sync with your current working tree.
Without it, CodeMap doesn't see any code you've added or changed this session.

**If a search returns nothing for code you just wrote:**
→ Run `index_refresh_overlay` first. Do NOT fall back to grep.
→ After refresh, retry the search. It will find the symbol.

---

### Session End (Optional)

```
workspace_delete { repo_path: ".", workspace_id: "session" }
```

After committing: `index_ensure_baseline { repo_path: "." }` to index the new commit.
