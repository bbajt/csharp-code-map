## Code Navigation — CodeMap MCP

**This project has CodeMap MCP enabled. If the codebase is C#/.NET:**
**STOP before reaching for `grep`, `cat`, `find`, `Get-ChildItem`, or any bash/shell
file search. CodeMap tools return precise semantic results without flooding context.**

---

### Session Start — Required Before Any Code Work

```
index.ensure_baseline  { repo_path: "." }
workspace.create       { repo_path: ".", workspace_id: "session" }
```

Fast (<2s total). Do this before reading any code or planning any changes.

---

### Decision Table — What to Use Instead

| You want to... | Use this — NOT bash |
|---|---|
| Find a class, method, interface by name | `symbols.search { query: "Name" }` |
| Understand what a method does + calls | `symbols.get_context { symbol_id: "..." }` |
| Read a method's source code | `symbols.get_card { symbol_id: "..." }` |
| Find who calls a method | `graph.callers { symbol_id: "..." }` |
| Find what a method calls | `graph.callees { symbol_id: "..." }` |
| Trace a feature end-to-end | `graph.trace_feature { entry_point: "..." }` |
| Find all usages of a symbol | `refs.find { symbol_id: "..." }` |
| Check what a class implements/inherits | `types.hierarchy { symbol_id: "..." }` |
| Search for text / string literals in source | `code.search_text { pattern: "...", repo_path: "." }` |
| Understand overall architecture | `codemap.summarize { repo_path: "." }` |
| See all HTTP endpoints | `surfaces.list_endpoints { repo_path: "." }` |
| See all config key usage | `surfaces.list_config_keys { repo_path: "." }` |
| See all DI registrations / DB tables | `surfaces.list_di_registrations` / `surfaces.list_db_tables` |

**Always include `workspace_id: "session"` in every query.**
Without it, queries only see committed code — in-progress edits are invisible.

---

### XML Doc Comments — Always Write Them

CodeMap indexes `/// <summary>` XML doc comments on all classes, methods,
interfaces, and properties. These appear in `symbols.get_card`,
`symbols.get_context`, and `symbols.search` results.

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
- `code.search_text` covers C# source text — use it before falling back to grep

---

### NEVER Type FQNs Manually

Roslyn doc-comment IDs are exact: `M:Namespace.Class.Method(ParamType)`.
A single wrong character returns NOT_FOUND.

**Always:** `symbols.search("MethodName")` → copy `symbol_id` from result.
**Never:** type `M:My.Namespace.Foo.Bar(System.String)` from memory.

---

### After Every File Edit — Refresh the Overlay

```
index.refresh_overlay { repo_path: ".", workspace_id: "session" }
```

Takes ~63ms. Keeps CodeMap in sync with your current working tree.
Without it, CodeMap doesn't see any code you've added or changed this session.

**If a search returns nothing for code you just wrote:**
→ Run `index.refresh_overlay` first. Do NOT fall back to grep.
→ After refresh, retry the search. It will find the symbol.

---

### Session End (Optional)

```
workspace.delete { repo_path: ".", workspace_id: "session" }
```

After committing: `index.ensure_baseline { repo_path: "." }` to index the new commit.
