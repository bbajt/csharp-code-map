# CodeMap MCP — Getting Started Guide for Claude Code

## Who This Guide Is For

You're a software developer (or aspiring one) who wants to use **Claude Code**
AI agent to build C# solutions. You've heard that CodeMap MCP can make the
AI agent smarter about your codebase — but you're not sure how to set it up
or how to tell the agent to use it.

This guide walks you through everything, step by step. No enterprise C#
experience required.

---

## What Is CodeMap MCP?

### The Problem

When you ask Claude Code to work on a C# project, the AI agent reads your
source files to understand the code. But reading files has fundamental
limitations — even with unlimited reading budget:

- **Reading a file tells you WHAT the code says, not WHAT IT MEANS.**
  Seeing `_service.Process(order)` in a file doesn't tell you what
  `Process` does, what type `_service` is, or where `Process` is defined.
  The agent has to guess or read more files.

- **Understanding relationships requires cross-referencing.**
  "Who calls this method?" requires scanning EVERY file in the project.
  "What implements this interface?" requires checking every class.
  "What's the full HTTP route for this action?" requires combining the
  class-level `[Route]` with the method-level `[HttpGet]` attribute.

- **Architectural questions are unanswerable from grep alone.**
  "What database tables does this service touch?" requires understanding
  EF Core's `DbSet<T>` pattern, `[Table]` attributes, AND raw SQL strings.
  "What's the middleware pipeline order?" requires finding all `app.Use*()`
  calls and knowing their sequence. No amount of text searching gives you
  these answers structured and complete.

### The Solution

CodeMap builds a **semantic index** of your C# solution using Roslyn (the
same compiler engine Visual Studio uses). It doesn't just store text — it
understands your code's structure, relationships, and architecture.

### What CodeMap Gives You That File Reading Cannot

| Capability | File Reading / Grep | CodeMap |
|-----------|-------------------|---------|
| Find a class by name | Grep for "class OrderService" — may miss partial classes, get false matches | `symbols.search("OrderService")` — exact match with metadata |
| Understand a method | Read the file, hope you have enough context | `symbols.get_card` — signature, docs, thrown exceptions, DI facts, config keys used |
| Who calls this method? | Grep for method name — gets string matches, not semantic calls | `refs.find` — classified references (Call, Read, Write, Override) with exact locations |
| Trace a call chain | Read file A, find calls, read file B, find its calls... manually, recursively | `graph.callers`/`graph.callees` — depth-limited traversal with cycle detection |
| What implements this interface? | Grep for `: IMyInterface` — misses multi-line declarations, generic constraints | `types.hierarchy` — complete inheritance tree across entire solution |
| List all HTTP endpoints | Grep for `[HttpGet]` — misses `MapGet`, can't combine with class `[Route]` | `surfaces.list_endpoints` — full route paths, HTTP methods, handler symbols |
| What config keys are used? | Grep for `Configuration[` — misses `GetValue<T>`, `GetSection`, `Configure<T>` | `surfaces.list_config_keys` — all 4 access patterns with usage context |
| What DB tables are touched? | Grep for `DbSet` and hope you find raw SQL too | `surfaces.list_db_tables` — EF entities + `[Table]` attributes + SQL table names, grouped |
| How is DI wired? | Read Startup.cs / Program.cs manually | `symbols.get_card` on setup method — sees `AddScoped<IService, Impl>` as structured facts |
| Trace a full feature | Read 10+ files, manually piece together the flow | `graph.trace_feature` — annotated call tree with endpoints, config, DB tables inline |
| Track changes in progress | Re-read all changed files | `workspace` + `index.refresh_overlay` — 63ms incremental re-index |
| Survive renames | Grep breaks completely when a method is renamed | `StableId` — structural fingerprint survives renames |
| Handle broken builds | Can't understand code that doesn't compile | Degrades gracefully to syntax-only extraction, self-heals when build succeeds |

### Token Savings Are a Side Effect

CodeMap responses are typically 90% smaller than reading raw source files.
But that's a consequence of the real value: **structured semantic
understanding.** The agent gets better answers AND uses fewer tokens.

On a small project (5-10 files), the token savings are modest. On a real
project with 50+ files, the savings compound — and more importantly, the
agent gets answers that would be impossible to assemble from file reading
alone.

### What Is MCP?

MCP stands for **Model Context Protocol**. It's a standard way for AI
agents to connect to external tools. Think of it like a USB port — MCP is
the standard, CodeMap is the device you plug in.

Claude Code already supports MCP. You just need to tell it where to find
the CodeMap server.

---

## Prerequisites

Before starting, make sure you have:

### 1. .NET 9 SDK

CodeMap is a .NET application. You need the SDK (not just the runtime).

**Check if installed:**
```bash
dotnet --version
```
Should show `9.0.x` or higher. If not installed:
- **Windows:** Download from https://dotnet.microsoft.com/download
- **macOS:** `brew install dotnet-sdk`
- **Linux:** Follow https://learn.microsoft.com/en-us/dotnet/core/install/linux

### 2. Git

CodeMap uses Git to track which version of your code is indexed.

**Check if installed:**
```bash
git --version
```

### 3. Claude Code

The AI agent that will use CodeMap.

**Check if installed:**
```bash
claude --version
```

If not installed, follow the official guide at https://docs.anthropic.com/en/docs/claude-code

### 4. A C# Solution

You need a `.sln` file with at least one `.csproj` project. If you're
starting from scratch, Claude Code can help you create one — and CodeMap
will index it as you build.

---

## Step 1: Get CodeMap

### Option A: Clone and Build (Recommended for Development)

```bash
# Clone the repository
git clone <codemap-repo-url>
cd CodeMap

# Build (this also restores NuGet packages)
dotnet build

# Verify it works
dotnet run --project src/CodeMap.Daemon -- --version
```

You should see something like `codemap-mcp 1.3.0`.

### Option B: Install as .NET Global Tool

```bash
# Install globally (once)
dotnet tool install --global codemap-mcp --version 1.3.0

# Verify
codemap-mcp --version
```

### Option C: Use Docker

```bash
# Build the Docker image
docker build -t codemap-mcp .

# Verify
docker run --rm codemap-mcp -- --version
```

---

## Step 2: Register CodeMap in Claude Code

This is the key step — you're telling Claude Code "here's a tool you can
use." You only need to do this once.

### Method 1: CLI Command (Simplest)

Open your terminal and run ONE of these depending on which installation
method you used:

**If you cloned and built (Option A):**
```bash
claude mcp add codemap -- dotnet run --no-build --project /full/path/to/CodeMap/src/CodeMap.Daemon
```

**If you installed as global tool (Option B):**
```bash
claude mcp add codemap -- codemap-mcp
```

**If you're using Docker (Option C):**
```bash
claude mcp add codemap -- docker run -i --rm -v /path/to/your/repos:/repos:ro codemap-mcp
```

### Method 2: Edit Config File Directly

If you prefer editing files, find your Claude Code config:

- **Project-level** (recommended): create `.mcp.json` in your project root
- **User-level**: `~/.claude.json`

Add this to the file (adjust the path to where you cloned/installed CodeMap):

```json
{
  "mcpServers": {
    "codemap": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "/full/path/to/CodeMap/src/CodeMap.Daemon"]
    }
  }
}
```

Or for the global tool:

```json
{
  "mcpServers": {
    "codemap": {
      "type": "stdio",
      "command": "codemap-mcp"
    }
  }
}
```

### Method 3: Project-Level .mcp.json (Best for Teams)

Create a `.mcp.json` file in your C# solution's root directory. This way,
every team member who uses Claude Code on this project automatically gets
CodeMap:

```json
{
  "mcpServers": {
    "codemap": {
      "type": "stdio",
      "command": "codemap-mcp"
    }
  }
}
```

Commit this file to your repo so the whole team benefits.

### Verify the Connection

Restart Claude Code (or open a new session), then check:

```
/mcp
```

You should see `codemap` listed as connected with its tools available.

---

## Step 3: Verify It Works

Open Claude Code in your C# project directory and try this conversation:

```
You: Use codemap to check the status of this repository.

Claude: I'll use the repo.status tool to check...
[calls repo.status with repo_path: "/path/to/your/project"]

Result: Repository at /path/to/your/project
  Branch: main
  Commit: abc123...
  Baseline index: false  ← No index yet
```

If you see a response like this, CodeMap is working. The "Baseline index:
false" is expected — we haven't built the index yet.

---

## Step 4: Build Your First Index

Tell the agent to index your solution:

```
You: Index this C# solution with codemap so you can understand the codebase.

Claude: I'll build a semantic index of your solution...
[calls index.ensure_baseline with repo_path and solution_path]

Result: Indexed 1,247 symbols, 3,891 references, 42 facts
  Semantic level: Full
  Time: 2.3 seconds
```

**What just happened:**
1. CodeMap opened your `.sln` file with Roslyn (the C# compiler)
2. It compiled every project in your solution
3. It extracted every class, method, property, interface, and their relationships
4. It stored everything in a local SQLite database
5. Now the agent can query this index instead of reading files

**The index is cached.** Next time you (or the agent) ask to ensure the
baseline, it returns instantly — no recompilation needed.

---

## Step 5: Start Using CodeMap in Your Workflow

Now the powerful part. Here are real conversations showing how CodeMap
changes the way the AI agent works with your code.

### Example 1: Understanding a Codebase You Didn't Write

```
You: I just joined this project. Can you give me an overview of the
     main services and what endpoints are exposed?

Claude: Let me search the codebase using codemap...
[calls symbols.search with query: "Service", kinds: ["Class"]]
[calls surfaces.list_endpoints]

Here's what I found:
- OrderService — handles order creation, validation, and fulfillment
- PaymentService — processes payments via Stripe integration
- NotificationService — sends emails and push notifications

The API exposes 12 endpoints:
  GET  /api/orders          — list orders
  POST /api/orders          — create order
  GET  /api/orders/{id}     — get order details
  ...

Would you like me to trace the flow of any specific endpoint?
```

**Without CodeMap:** The agent would read 30+ files, use thousands of tokens,
and likely miss some services or endpoints.

### Example 2: Tracing a Feature End-to-End

```
You: Trace what happens when a user creates an order. I want to see
     the full flow from endpoint to database.

Claude: I'll trace the feature using codemap...
[calls graph.trace_feature with entry_point: "OrderController.Create", depth: 3]

Feature trace: POST /api/orders
  → OrderController.Create [POST /api/orders]
    → OrderService.CreateOrderAsync
      → [Config: App:MaxOrderItems]
      → OrderValidator.ValidateAsync
      → Repository<Order>.AddAsync
        → [DB: Orders (DbSet<Order>)]
      → PaymentService.ChargeAsync
      → NotificationService.SendOrderConfirmation
        → [Config: Smtp:Host]

The order creation flow:
1. Controller receives the request
2. Service validates the order (max items from config)
3. Saves to the Orders database table
4. Charges payment
5. Sends confirmation email (SMTP config)
```

### Example 3: Making Changes Safely

```
You: I need to add a new "discount" field to the Order class.
     What files will I need to change?

Claude: Let me check what references the Order class...
[calls refs.find with symbol_id for Order class]
[calls types.hierarchy for Order]

The Order class is referenced in 14 places:
- OrderService.CreateOrderAsync (line 45) — creates new Order
- OrderController.Create (line 23) — maps from DTO
- OrderValidator.ValidateAsync (line 12) — validates fields
- Repository<Order> — database persistence
- OrderTests.cs — 3 test methods

You'll need to update:
1. Order.cs — add the Discount property
2. CreateOrderRequest DTO — add Discount field
3. OrderController.Create — map Discount from request
4. OrderValidator — add discount validation rules
5. OrderTests — update test data

Want me to make these changes?
```

### Example 4: Working with Workspaces (In-Progress Edits)

When the agent makes changes, CodeMap can track them in real-time without
rebuilding the entire index:

```
You: Create a workspace so you can track your changes, then add
     that discount field to Order.

Claude: I'll create an isolated workspace for these changes...
[calls workspace.create with workspace_id: "add-discount"]

Now I'll make the changes...
[edits Order.cs, CreateOrderRequest.cs, etc.]

Let me refresh the index to see the updated codebase...
[calls index.refresh_overlay with workspace_id: "add-discount"]

The index now reflects your changes. Let me verify:
[calls symbols.get_card for Order class with workspace_id]

Order now has 7 properties (was 6):
  Id, CustomerId, Items, Total, Status, CreatedAt, Discount ← new

All references are consistent. Want me to update the tests?
```

### Example 5: Checking Configuration Usage

```
You: What configuration keys does this application use?
     I want to make sure our appsettings.json has everything.

Claude: Let me check...
[calls surfaces.list_config_keys]

Configuration keys found:
- ConnectionStrings:DefaultDB — used in DataContext (IConfiguration indexer)
- App:MaxOrderItems — used in OrderValidator (GetValue<int>)
- Smtp:Host — used in EmailService (GetSection)
- Smtp:Port — used in EmailService (GetValue<int>)
- Stripe:ApiKey — used in PaymentService (IConfiguration indexer)
- Logging:LogLevel:Default — used in Program.cs (GetSection)

Make sure your appsettings.json includes all of these.
```

### Example 6: Generate Context for Another AI Model

```
You: Export the codebase architecture so I can paste it into ChatGPT
     for a second opinion on my refactoring plan.

Claude: I'll export a standard-detail summary...
[calls codemap.export(detail: "standard", format: "markdown", max_tokens: 6000)]

Here's your codebase context (estimated 4,200 tokens). Copy everything
between the markers and paste into ChatGPT or any other AI:

--- COPY FROM HERE ---
# MyProject — Codebase Summary
## API Surface (12 endpoints)
  GET  /api/orders        → OrdersController.GetAll
  POST /api/orders        → OrdersController.Create
  ...
## Public API Surface
### OrderService
  - SubmitAsync(Order): Task<OrderResult>
  - ValidateAsync(Order): Task
  Calls: Repository<Order>.SaveAsync, EmailService.SendConfirmation
...
--- COPY TO HERE ---
```

### Example 7: Review a PR with Semantic Diff

```
You: What changed semantically in the last commit? I want to review
     the PR before merging.

Claude: [calls index.diff(from: "previous_sha", to: "HEAD")]

Semantic Diff: abc1234 → def5678

Summary:
- 2 symbols added (PaymentService, IPaymentService)
- 1 method renamed (Submit → SubmitOrder)
- 1 endpoint added (POST /api/payments)
- 1 config key added (Stripe:ApiKey)
- 1 DI registration added (IPaymentService → PaymentService, Scoped)

The PR adds a payment processing feature with its own service,
endpoint, config, and DI wiring. All changes look consistent.
```

---

### Example 8: Deep-Dive a Method with Full Context

```
You: I need to understand what SubmitAsync does and everything
     it calls. Give me the full picture in one shot.

Claude: [calls symbols.get_context(OrderService.SubmitAsync, callee_depth: 1)]

# OrderService.SubmitAsync
File: src/SampleApp/Services/OrderService.cs (lines 23-45)
Signature: `public async Task<OrderResult> SubmitAsync(Order order)`

Facts:
- Exception: ArgumentNullException (nameof guard)
- Log: Order {OrderId} submitted (Information)

## Source Code
```csharp
public async Task<OrderResult> SubmitAsync(Order order)
{
    ArgumentNullException.ThrowIfNull(order);
    var validated = await _validator.ValidateAsync(order);
    var saved = await _repository.AddAsync(order);
    _logger.LogInformation("Order {OrderId} submitted", saved.Id);
    return new OrderResult(saved.Id, saved.Status);
}
```

## Callees (3 methods)

### OrderValidator.ValidateAsync (lines 12-28)
```csharp
public async Task ValidateAsync(Order order) { ... }
```

### Repository<Order>.AddAsync (lines 34-42)
```csharp
public async Task<T> AddAsync(T entity) { ... }
```

One call — I now understand the method, its error handling,
its logging, and what each callee does.
```

---

### Example 9: Verify Interface Compatibility Before Implementation

```
You: I need to implement IReadOnlyDictionary<TKey, TValue> on our
     BPlusTree class. Verify what's already there before I start.

Claude: [calls types.hierarchy(T:BPlusTree.Core.BPlusTree`2)]

BPlusTree currently implements: IEnumerable<T>, IDisposable
Does NOT implement: IReadOnlyDictionary

[calls symbols.search("BPlusTree TryGetValue")]
→ Found: BPlusTree.TryGetValue(TKey, out TValue) ✅

[calls symbols.search("BPlusTree ContainsKey")]
→ No matches ❌

[calls symbols.search("BPlusTree Keys")]
→ No matches ❌

[calls symbols.search("BPlusTree Values")]
→ No matches ❌

[calls symbols.get_card(T:BPlusTree.Core.BPlusTree`2)]
→ Members include: Add, Remove, TryGetValue, Count, Clear, GetEnumerator

Verification summary (9 checks, 0 file reads):
- Count property ✅
- TryGetValue ✅
- ContainsKey — MISSING ❌
- Keys property — MISSING ❌
- Values property — MISSING ❌
- IReadOnlyDictionary declaration — NOT YET ❌

Action plan: Add ContainsKey, Keys, Values, then add
IReadOnlyDictionary<TKey, TValue> to the class declaration.
```

---

## Tips for Getting the Best Results

### Upgrading from v1.x to v2.0

CodeMap v2.0 uses a new binary storage engine (memory-mapped segments instead of SQLite).
Your existing `.db` baselines are **not auto-migrated** — they're simply ignored by the
new engine. After upgrading:

1. Run `index.ensure_baseline` once per repo — rebuilds the index in v2 format (~15s)
2. Old `.db` files are harmless — clean them with `index.cleanup` when ready
3. Need the old engine? Set `CODEMAP_ENGINE=sqlite` in your environment

v2 baselines live in `~/.codemap/store/<repoId>/baselines/<sha>/` (directories with
segment files), not `~/.codemap/baselines/<repo>/<sha>.db` (single SQLite files).

### 1. Always Index First

Before asking the agent to work on your code, tell it to index:

```
Before we start, use codemap to index this solution so you can
understand the codebase efficiently.
```

### 2. Ask Architectural Questions, Not Just "Find Me X"

CodeMap's biggest advantage over grep is answering **architectural**
questions. Instead of asking the agent to read files, ask it to reason:

```
BAD:  "Read OrderService.cs and tell me what it does"
GOOD: "Use codemap to get the card for OrderService — what are its
       dependencies, what does it call, what config does it use,
       and what database tables does it touch?"
```

```
BAD:  "Search for all files containing IConfiguration"
GOOD: "Use codemap to list all configuration keys used in the solution,
       grouped by which service uses them"
```

```
BAD:  "Find all classes that implement IRepository"
GOOD: "Use codemap's type hierarchy to show me everything that
       implements IRepository, then trace the callers of each
       implementation to see which services depend on which repos"
```

### 3. Use Feature Tracing for Understanding Flows

This is CodeMap's most powerful capability. Instead of asking the agent
to manually read files and piece together a flow:

```
Use codemap to trace the complete flow of the password reset feature,
starting from the API endpoint. I want to see every method call,
every config key read, every database table touched, and every
external service called.
```

The agent gets a single annotated tree showing the entire feature
in one query — something that would take 10+ file reads to assemble.

### 4. Use Reference Classification for Impact Analysis

When planning changes, don't just ask "who uses this?" — ask for
classified references:

```
I'm planning to change the signature of OrderService.SubmitAsync.
Use codemap to find all references — I need to know which ones are
direct calls vs overrides vs interface implementations, because
each type of reference needs a different update strategy.
```

### 5. Use Workspaces for Multi-File Changes

When making changes across multiple files, workspaces let the agent
verify consistency as it goes:

```
Create a codemap workspace called "feature-xyz", make the changes,
and refresh the overlay after each batch of edits so you can verify
the changes are consistent — check that no references are broken
and the type hierarchy is still valid.
```

### 6. Use Surface Tools for Audits

Before a release or code review, use the surface tools to audit the
codebase holistically:

```
I need a full architectural audit before we release. Use codemap to:
1. List all HTTP endpoints — are any missing authentication?
2. List all config keys — are any hardcoded instead of configurable?
3. List all DB tables — are any accessed without going through a repository?
4. Check the middleware pipeline — is authorization before authentication?
5. Check retry policies — are all external calls resilient?
```

### 7. Use Caller/Callee Graphs for Safe Refactoring

Before moving or renaming code:

```
I want to extract OrderValidator into its own project.
Use codemap's caller graph (depth 3) to find everything that
calls OrderValidator, and callee graph to find everything it
depends on. This tells me exactly what interfaces I need to
expose and what references will break.
```

### 8. Let the Agent Chain Tools Naturally

The most powerful results come from the agent combining multiple tools.
Give it the goal, not the tool sequence:

```
I need to understand why order creation is slow. Help me investigate
by tracing the full flow, checking what database queries are involved,
and identifying any config values that might control timeouts or
retry counts.
```

The agent will naturally chain `graph.trace_feature` → `surfaces.list_db_tables`
→ `surfaces.list_config_keys` → `refs.find` to build a complete picture.

### 9. Use get_context Instead of Manual Exploration

Instead of chaining `get_card → get_definition_span → graph.callees → get_card`:

```
Use codemap's symbols.get_context to give me the full picture of
SubmitAsync — the code, what it calls, and the code of each callee.
```

One call replaces 5–7 manual lookups. Use `callee_depth: 2` for deeper
chains (e.g., "trace two levels of calls from this method").

### 10. Always Use symbol_id from Search Results — Never Construct FQNs Manually

Roslyn documentation comment IDs have precise formatting rules that are
easy to get wrong:

- Methods: `M:Namespace.Class.Method(FullParamType1,FullParamType2)`
- Generics: `List{T}` not `List<T>`
- Overloads: must include complete parameter types to disambiguate
- Nested types: `OuterClass.InnerClass`, not just `InnerClass`

A manually-typed FQN that's even slightly off returns `NOT_FOUND`.

**The reliable pattern:**

```
1. symbols.search("MethodName")          → get the exact symbol_id from results
2. symbols.get_context(symbol_id: ...)   → use that ID directly
```

The `next_actions` in every search response contain ready-to-use `symbol_id`
values. Follow them instead of constructing IDs from memory.

### 11. Navigate into SDK and NuGet DLLs

CodeMap resolves DLL boundaries lazily — when an agent searches for a symbol
from a referenced assembly, CodeMap automatically extracts metadata stubs on
first access without any pre-configuration:

```
You: How does Polly's WaitAndRetryAsync work? Trace from our ResilienceSetup
     through the Polly implementation.

Claude: Let me look up the Polly type...
[calls symbols.search("WaitAndRetryAsync")]
→ Found: M:Polly.Policy.WaitAndRetryAsync(...) — source: "metadata_stub"
  (Level 1 auto-resolved: signature + XML doc, ~3ms)

[calls symbols.get_card with include_code: true]
→ source: "decompiled" — reconstructed C# source now visible
  (Level 2 decompiled: full source via ICSharpCode.Decompiler, ~45ms, cached forever)

[calls graph.callees on WaitAndRetryAsync, depth: 2]
→ Returns Polly-internal call chain — cross-DLL edges extracted during decompilation

[calls graph.trace_feature from ResilienceSetup.ConfigureRetry]
→ Full annotated tree: source code → Polly DLL nodes (marked is_decompiled: true)
```

**Key behaviours:**
- Level 1 (metadata stub): always active, ~1–5ms. Gets signatures + XML docs.
- Level 2 (decompiled source): triggered by `include_code: true`, ~10–200ms once,
  then 0ms from cache. The `source` field in `symbols.get_card` tells you which
  level applied: `"source_code"`, `"metadata_stub"`, or `"decompiled"`.
- After Level 2, `graph.callees` and `graph.trace_feature` traverse INTO DLL code.
- `graph.trace_feature` applies a `max_lazy_resolutions_per_query` budget (default 20)
  to bound decompilation latency during a single traversal query.

### 12. Clean Up Old Baselines Periodically

Baselines accumulate from every indexed commit and are never automatically
evicted. On active repos, this can reach 50–100 MB over time. Run cleanup
monthly or whenever disk space is a concern:

```
Use codemap to clean up old baselines, keeping the 3 most recent.
```

The agent will call `index.cleanup(dry_run: true, keep_count: 3)` first to
show what would be removed, then confirm before deleting. The current HEAD
baseline and any workspace-referenced baselines are always protected.

## How Indexing Works Across Git Branches

Understanding this unlocks CodeMap's full power for teams.

### One Index Per Commit, Not Per Branch

CodeMap indexes are keyed by **commit SHA**, not branch name. This has
important implications:

```
main         ── A ── B ── C        (HEAD = C)
                      \
feature-x              D ── E      (HEAD = E)
```

- Commit C gets baseline `~/.codemap/baselines/{repo}/C.db`
- Commit E gets baseline `~/.codemap/baselines/{repo}/E.db`
- Both exist simultaneously — switching branches doesn't rebuild anything

**Switching branches is instant:**
```
You: I'm switching to the feature-x branch. Re-index with codemap.

Claude: [calls index.ensure_baseline]
Result: Baseline already exists for commit E (cached). Ready instantly.
```

If you switch to a commit that hasn't been indexed yet, CodeMap builds it
(or pulls from the shared cache). Once built, it stays cached forever.

### What Happens When You Switch Branches

```
1. You're on main (commit C) — baseline C indexed, workspace active
2. You switch to feature-x (commit E)
3. HEAD is now E, but your workspace was created against C
4. workspace.list shows: IsStale = true (C ≠ E)
5. You (or the agent) creates a new workspace against E
6. The old baseline C is still there — switch back and it's instant
```

The agent handles this naturally:

```
You: I just switched to the feature-x branch. Set up codemap.

Claude: Let me check the status...
[calls repo.status]
Current branch: feature-x, commit: E
Baseline exists: false — let me build it.
[calls index.ensure_baseline]
Indexed 1,300 symbols in 2.1 seconds.

I also see a stale workspace from your main branch work.
Want me to delete it and create a fresh one for feature-x?
```

### Parallel Branch Development

If you're working on multiple branches (or reviewing PRs), each branch
has its own baseline. No interference:

```
~/.codemap/baselines/{repo}/
  abc123.db    ← main at last commit
  def456.db    ← feature-auth branch
  789fed.db    ← feature-payments branch
  112233.db    ← PR #42 you reviewed yesterday
```

All cached. Switching between any of them is instant. With the shared
cache, your teammate's branches are available without rebuilding too.

---

## Multi-Agent Workflows

This is where CodeMap's workspace architecture really shines. A
**supervisor** agent can coordinate multiple **sub-agents**, each working
on different parts of the codebase simultaneously.

### Why Multi-Agent?

Large tasks benefit from parallel work:
- Agent 1 refactors the data layer
- Agent 2 adds new API endpoints
- Agent 3 writes tests for the existing code
- Supervisor coordinates, monitors quality, and merges

Without CodeMap, each agent reads the same files independently — wasting
tokens and potentially making conflicting changes without knowing it.

With CodeMap, each agent has an **isolated workspace overlay** that tracks
only its changes, while sharing the same baseline index.

### How It Works

```
                     Baseline Index (commit abc123)
                     ┌──────────────────────────┐
                     │ All symbols, refs, facts  │
                     │ (immutable, shared)        │
                     └──────┬──────┬──────┬──────┘
                            │      │      │
                     ┌──────┴──┐ ┌─┴────┐ ┌┴───────┐
                     │Agent-1  │ │Agent-2│ │Agent-3  │
                     │Overlay  │ │Overlay│ │Overlay  │
                     │(data    │ │(API   │ │(tests)  │
                     │ layer)  │ │routes)│ │         │
                     └─────────┘ └──────┘ └─────────┘
```

Each overlay:
- Tracks only the files THAT agent changed
- Is invisible to other agents (full isolation)
- Merges with the shared baseline when queried
- Re-indexes in ~63ms after edits (not the full 2-30 seconds)

### Supervisor Workflow Step by Step

```
You: I need to refactor the order system. Set up a multi-agent workflow
     where one agent handles the data layer and another handles the API.

Claude (supervisor): I'll set up the workspace...

# Step 1: Ensure the baseline is indexed
[calls index.ensure_baseline]
Baseline ready: 1,247 symbols at commit abc123.

# Step 2: Create isolated workspaces for each agent
[calls workspace.create with workspace_id: "agent-data"]
[calls workspace.create with workspace_id: "agent-api"]

# Step 3: Review the current architecture before assigning work
[calls surfaces.list_endpoints]
[calls surfaces.list_db_tables]
[calls types.hierarchy for IRepository]

Here's the plan:
- agent-data workspace: refactor Repository<T>, update Order entity,
  migrate DbContext
- agent-api workspace: update OrderController, add new endpoints,
  update DTOs

# Step 4: Each agent works in its isolated workspace
# (agent-data edits files, refreshes overlay)
[calls index.refresh_overlay with workspace_id: "agent-data"]
# (agent-api edits files, refreshes overlay)
[calls index.refresh_overlay with workspace_id: "agent-api"]

# Step 5: Supervisor monitors progress
[calls workspace.list]
Workspaces:
  agent-data: revision 3, 5 files modified, SemanticLevel: Full, FactCount: 12
  agent-api:  revision 2, 3 files modified, SemanticLevel: Full, FactCount: 8
  Both fresh (IsStale: false)

# Step 6: Verify each workspace's changes are consistent
[calls surfaces.list_endpoints with workspace_id: "agent-api"]
→ new endpoints visible, old ones unchanged

[calls surfaces.list_db_tables with workspace_id: "agent-data"]
→ updated table schema visible

# Step 7: After agents commit, clean up
[calls workspace.delete with workspace_id: "agent-data"]
[calls workspace.delete with workspace_id: "agent-api"]
[calls index.ensure_baseline]  ← new baseline for merge commit
```

### Stale Detection: When the Branch Moves

If someone pushes a new commit while agents are working:

```
# Before: workspaces created against commit abc123
# Someone pushes commit def456 to main

[calls workspace.list]
Workspaces:
  agent-data: BaseCommit=abc123, IsStale=true  ← HEAD moved!
  agent-api:  BaseCommit=abc123, IsStale=true

# The supervisor can decide:
# Option A: Continue working (stale is fine if changes don't conflict)
# Option B: Rebuild workspaces against the new commit
[calls workspace.delete with workspace_id: "agent-data"]
[calls index.ensure_baseline]  ← builds baseline for def456
[calls workspace.create with workspace_id: "agent-data"]
# Agent re-applies its changes to the new baseline
```

### Quality Monitoring

The supervisor can check each workspace's index quality:

```
[calls workspace.list]

Workspaces:
  agent-1: SemanticLevel=Full, FactCount=15
    → Index is complete, all projects compiled
  
  agent-2: SemanticLevel=Partial, FactCount=3
    → Something is broken — some projects didn't compile
    → Fewer facts extracted (broken code = fewer detected patterns)
  
  agent-3: SemanticLevel=SyntaxOnly, FactCount=0
    → Nothing compiled — agent likely introduced a build error
    → References are unresolved, no facts extracted
```

The supervisor can tell agent-3: "Your changes broke the build. Fix the
compilation errors first, then refresh the overlay."

When agent-3 fixes the code and refreshes, CodeMap's **resolution worker**
automatically upgrades the previously unresolved references to resolved —
no manual intervention.

### Benefits of Multi-Agent with CodeMap

| Aspect | Without CodeMap | With CodeMap |
|--------|----------------|-------------|
| Isolation | Agents overwrite each other's files | Each agent has its own overlay |
| Visibility | Supervisor can't see what agents changed | workspace.list shows file count, quality, staleness |
| Consistency | No way to check if changes are compatible | Query each workspace's endpoints, refs, DB tables independently |
| Reindexing | Full rebuild after every change | 63ms incremental refresh per workspace |
| Branch handling | Manual tracking of which commit each agent targets | Automatic stale detection |
| Quality signals | None until build breaks | SemanticLevel + FactCount per workspace |
| Self-healing | Broken builds = broken state until full rebuild | Resolution worker auto-upgrades edges when build succeeds |

---

## Shared Cache: Speed Up Team Indexing

If your team all works on the same repo, you can share the index so
only one person (or CI) needs to build it:

### Set Up a Shared Cache Directory

```bash
# On a shared network drive, or a local directory for solo use
export CODEMAP_CACHE_DIR=/shared/codemap-cache
```

Add this to your shell profile (`.bashrc`, `.zshrc`, etc.) so it persists.

### How It Works

1. First person runs `index.ensure_baseline` → CodeMap builds the index
   (~2-30 seconds depending on solution size) and automatically pushes
   it to the shared cache
2. Second person runs `index.ensure_baseline` → CodeMap finds it in the
   shared cache, pulls it in ~2 milliseconds. No compilation needed.
3. When someone pushes a new commit, the next person to index automatically
   builds and shares the new version

This is especially powerful in **CI/CD pipelines** — your build server
indexes every commit, and developers instantly get the index.

---

## Troubleshooting

### "CodeMap tools not showing up in Claude Code"

1. Check the MCP connection: type `/mcp` in Claude Code
2. If not listed, verify your config:
   - CLI: `claude mcp list` — should show `codemap`
   - If missing, re-run the `claude mcp add` command
3. Restart Claude Code after adding the MCP server

### "index.ensure_baseline fails"

1. Make sure your `.sln` file is in the repository root (or provide the
   full path)
2. Check that `dotnet build` succeeds on its own — if your solution
   doesn't compile, CodeMap can still index it (degraded mode) but you
   may see fewer results
3. Check that .NET 9 SDK is installed: `dotnet --version`

### "The agent isn't using CodeMap"

Sometimes the agent may try to read files directly instead of using
CodeMap. You can guide it:

```
Don't read the source files directly. Use codemap tools to search
and query the codebase — it's much more efficient.
```

### "Index seems outdated after code changes"

The baseline index is per-commit. If you've made uncommitted changes:

```
Create a codemap workspace and refresh the overlay to include
my latest changes.
```

Or if you've committed new changes:

```
Rebuild the codemap baseline index — I've made new commits.
```

---

## Quick Reference: All 26 CodeMap Tools

### Understanding Your Codebase

| Tool | What It Does | Example Prompt |
|------|-------------|----------------|
| `symbols.search` | Find symbols by name, kind, namespace | "Find all classes named *Service" |
| `symbols.get_card` | Structured metadata + facts + **source code** (auto-included). Default choice. | "Tell me everything about OrderService" |
| `symbols.get_context` | Card + code + callee cards with code | "Explain this method fully" |
| `code.get_span` | Read specific source lines | "Show me lines 10-30 of OrderService.cs" |
| `code.search_text` | Regex grep across indexed source files | "Find all files containing 'OrderNotFoundException'" |
| `symbols.get_definition_span` | Source code only, no card overhead. Use for batch reads. | "Just show me the code for SubmitAsync" |

### Navigating Relationships

| Tool | What It Does | Example Prompt |
|------|-------------|----------------|
| `refs.find` | Classified references (Call, Read, Write, Override) | "Who calls SubmitAsync and how?" |
| `graph.callers` | Trace callers upward (depth-limited) | "What triggers this method?" |
| `graph.callees` | Trace callees downward (depth-limited) | "What does this method do internally?" |
| `types.hierarchy` | Inheritance: base type, interfaces, derived | "What implements IRepository?" |
| `graph.trace_feature` | Full annotated feature flow | "Trace the order creation flow end-to-end" |

### Architectural Surfaces

| Tool | What It Does | Example Prompt |
|------|-------------|----------------|
| `surfaces.list_endpoints` | All HTTP routes (controllers + minimal API) | "What API endpoints exist?" |
| `surfaces.list_config_keys` | All configuration key usage | "What config does this app need?" |
| `surfaces.list_db_tables` | All database tables (EF + raw SQL) | "What DB tables are accessed?" |

### Intelligence & Portability

| Tool | What It Does | Example Prompt |
|------|-------------|----------------|
| `codemap.summarize` | Architectural overview (10 sections) | "Give me a codebase overview" |
| `codemap.export` | Self-contained markdown/JSON for any LLM | "Export this for ChatGPT review" |
| `index.diff` | Semantic diff between two commits | "What changed since last week?" |

### Managing the Index

| Tool | What It Does | Example Prompt |
|------|-------------|----------------|
| `repo.status` | Git state + index status | "Is the index up to date?" |
| `index.ensure_baseline` | Build or pull the semantic index | "Index this solution" |
| `index.list_baselines` | List cached baselines with size and age | "How many baselines are stored?" |
| `index.cleanup` | Remove old baselines safely (dry-run default) | "Clean up old indexes" |

### Workspace (In-Progress Edits)

| Tool | What It Does | Example Prompt |
|------|-------------|----------------|
| `workspace.create` | Start tracking agent's edits | "Create a workspace for this feature" |
| `workspace.reset` | Discard tracked changes | "Start over on this workspace" |
| `workspace.list` | See all workspaces + staleness | "Show me all active workspaces" |
| `workspace.delete` | Remove a workspace completely | "Clean up the old workspace" |
| `index.refresh_overlay` | Re-index after file changes (~63ms) | "Update the index with my changes" |

---

## Where Everything Lives

CodeMap stores all data under `~/.codemap/` in your home directory. Here's
the complete layout:

```
~/.codemap/
│
├── store/                               ← v2 binary baselines (default engine)
│   └── {repo-id}/
│       └── baselines/
│           └── {commit-sha}/            ← One directory per indexed commit
│               ├── manifest.json        ← Metadata, checksums
│               ├── symbols.seg          ← Symbol records (mmap'd)
│               ├── edges.seg, facts.seg ← References + architectural facts
│               ├── search.idx           ← Token search index
│               └── ...                  ← Other segment files
│
├── baselines/                           ← SQLite baselines (CODEMAP_ENGINE=sqlite)
│   └── {repo-id}/
│       └── {commit-sha}.db             ← One .db per indexed commit
│
├── overlays/                            ← Workspace overlay databases
│   └── {repo-id}/
│       └── agent-1.db                   ← Workspace overlay
│
├── logs/                                ← Structured JSON log files
│   ├── codemap-2026-03-05.log           ← One file per day
│   └── codemap-2026-03-06.log
│
├── config.json                          ← Settings (log level, cache dir, budgets)
├── _savings.json                        ← Cumulative token savings counter
│
└── bin/                                 ← Published daemon binary (if installed locally)
    └── CodeMap.Daemon.dll
```

### Understanding Repository IDs

The folder name under `store/` (v2) or `baselines/` (SQLite) is a sanitized
repo ID derived from your repository's path. For example:

```
C:\Users\Developer\source\repos\MyProject
  → repo ID: local-b73d0e677cfb93f6
  → v2 baselines at: ~/.codemap/store/local-b73d0e677cfb93f6/baselines/
```

### Understanding Baseline Files

**v2 (default):** Each baseline is a directory of memory-mapped binary segments.
**SQLite (fallback):** Each `.db` is a SQLite database with the semantic index
for one specific commit:

```
~/.codemap/baselines/local-b73d0e677cfb93f6/
  e9cdd20f...b6.db     ← 2.1 MB — indexed yesterday (old commit)
  03e6c1c9...6c.db     ← 2.3 MB — indexed today (current HEAD)
```

Key facts:
- **One database per commit SHA.** Switch branches, new baseline. Push a
  commit, new baseline. Each is independent.
- **Baselines are immutable.** Once created, never modified. Safe to read
  from multiple agents simultaneously.
- **Old baselines accumulate.** They're never automatically deleted. Over
  weeks of active development, you may accumulate dozens of baseline files.
- **Typical size:** 1-5 MB per baseline for a medium C# solution.
  A solution with 1,000 symbols and 5,000 references produces ~2-3 MB.
- **WAL files (-shm, -wal)** are SQLite working files. They appear when
  a baseline is being read and disappear when all connections close. If you
  see stale WAL files after a crash, they're safe to delete — SQLite will
  recover on next open.

### Understanding Overlay Files

Workspace overlays track in-progress agent edits:

```
~/.codemap/overlays/local-b73d0e677cfb93f6/
  agent-1.db            ← Agent 1's tracked changes
  agent-2.db            ← Agent 2's tracked changes
```

These are created by `workspace.create` and deleted by `workspace.delete`.
They're small (usually < 500 KB) because they only store the diff against
the baseline.

### Shared Cache (Optional)

If you set `CODEMAP_CACHE_DIR`, baselines are also copied there:

```
/shared/codemap-cache/                   ← Network drive or shared folder
  └── local-b73d0e677cfb93f6/
      ├── abc123...def.db                ← Shared baseline (pushed after build)
      └── def456...789.db                ← Another shared baseline
```

This is a mirror of your local baselines — same structure, same files.
Other developers pull from here instead of rebuilding.

### Managing Disk Space

Over time, old baselines accumulate. To check how much space CodeMap uses:

**Windows:**
```powershell
# Total size
(Get-ChildItem -Path "$env:USERPROFILE\.codemap" -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB

# Size per repo
Get-ChildItem -Path "$env:USERPROFILE\.codemap\baselines" -Directory |
  ForEach-Object { "$($_.Name): $([math]::Round((Get-ChildItem $_.FullName -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)) MB" }

# Count baselines per repo
Get-ChildItem -Path "$env:USERPROFILE\.codemap\baselines\*\*.db" |
  Group-Object Directory |
  ForEach-Object { "$($_.Name | Split-Path -Leaf): $($_.Count) baselines" }
```

**macOS / Linux:**
```bash
# Total size
du -sh ~/.codemap/

# Size per repo
du -sh ~/.codemap/baselines/*/

# Count baselines per repo
for d in ~/.codemap/baselines/*/; do echo "$(basename $d): $(ls $d/*.db 2>/dev/null | wc -l) baselines"; done
```

**Manual cleanup — remove old baselines:**

```bash
# WARNING: only do this when CodeMap daemon is NOT running

# Delete ALL baselines for a repo (forces full reindex next time)
rm -rf ~/.codemap/baselines/local-b73d0e677cfb93f6/

# Keep only the most recent baseline (by modification time)
cd ~/.codemap/baselines/local-b73d0e677cfb93f6/
ls -t *.db | tail -n +2 | xargs rm -f   # Keeps newest, deletes rest

# Delete baselines older than 7 days
find ~/.codemap/baselines/ -name "*.db" -mtime +7 -delete
```

> **Tip:** Use `index.list_baselines` to see all cached baselines with
> their size and age, and `index.cleanup` to safely remove old ones
> (dry-run by default — reports what would be deleted without deleting).
> Both tools protect the current HEAD baseline and any workspace-referenced
> baselines from deletion. For manual cleanup, use the commands above.

**Automated cleanup via MCP (recommended):**

Instead of shell commands, let the agent handle it safely:

```
You: Clean up old codemap baselines, keep only the 3 most recent.

Claude: [calls index.cleanup(dry_run: true, keep_count: 3)]
Would remove 7 baselines (18 MB). HEAD and workspace baselines protected.

You: Go ahead.

Claude: [calls index.cleanup(dry_run: false, keep_count: 3)]
Removed 7 baselines, reclaimed 18 MB.
```

The tool always protects the current HEAD and any workspace baselines —
safe to run without reviewing individual files first.

---

## What Happens Under the Hood

For the curious — here's what CodeMap does when you index a solution:

```
Your .sln file
     │
     ▼
Roslyn Compiler (same engine Visual Studio uses)
     │
     ├── Compiles every project (understands C# semantics, not just text)
     │
     ├── Symbol Extraction
     │     Every class, struct, interface, enum, method, property, field,
     │     event — with full signature, visibility, documentation, generics
     │
     ├── Reference Classification
     │     Every usage of every symbol, classified: Call, Read, Write,
     │     Instantiate, Override, Implementation — not just "text match"
     │
     ├── Type Hierarchy
     │     Base types, implemented interfaces, derived types — across
     │     ALL projects in the solution, not just the current file
     │
     ├── Architectural Fact Extraction
     │     ├── HTTP endpoints ([HttpGet], MapGet → full route paths)
     │     ├── Config key usage (IConfiguration["key"], GetValue, GetSection)
     │     ├── Database tables (DbSet<T>, [Table], raw SQL patterns)
     │     ├── DI registrations (AddScoped, AddSingleton → service → impl)
     │     ├── Middleware pipeline (Use* call order with position tracking)
     │     └── Retry policies (Polly RetryAsync, Resilience AddRetry)
     │
     └── Stable Identity
           Each symbol gets a structural fingerprint (StableId) that
           survives renames — same signature = same identity
     │
     ▼
SQLite Database (~1-5 MB per solution)
  ├── symbols table (with FTS5 full-text search)
  ├── refs table (classified, with resolution state)
  ├── type_relations table
  ├── facts table (architectural surfaces)
  └── files table (path + hash for change detection)
     │
     ▼
19 MCP Tools — the agent queries structured data, not raw files
```

The index is stored locally at `~/.codemap/baselines/` and is immutable
per commit. When you switch branches or pull new code, a new index is
built for the new commit (or pulled from the shared cache instantly).

**Key insight:** CodeMap doesn't just index text — it understands C#
semantics. It knows that `_service.Process(order)` is a `Call` reference
to `IOrderService.Process`, that `order.Status = "Complete"` is a `Write`
reference to `Order.Status`, and that `new Order()` is an `Instantiate`
reference to `Order`. Grep sees text patterns. CodeMap sees meaning.

---

## Tracking Token Savings

CodeMap tracks how many tokens it saves on every query. This helps you
see the value — but it's important to understand how the numbers work.

### How Savings Are Calculated

For each query, CodeMap estimates:

```
Raw tokens    = approximate cost of reading all source files in the solution
CodeMap tokens = size of the structured tool response
Savings       = (raw - codemap) / raw
```

Token estimation uses a simple `text.Length / 4` heuristic (roughly matching
how LLMs tokenize text).

**What "raw tokens" means:** It assumes that without CodeMap, an agent
would need to read ALL source files to answer a question. This is a
simplification — in practice, a smart agent might grep for specific files
rather than reading everything. So the savings number is an upper bound
for simple queries.

**Where the number is most accurate:** On large codebases (50+ files),
CodeMap's response size stays roughly constant (~200-400 tokens) while the
raw file reading cost grows linearly. A 100-file solution might have 25,000
tokens of source — CodeMap still returns 300 tokens. That's 98%+ savings,
and it's genuinely accurate because an agent really would need to read
many files to piece together the same information.

**The headline "90% savings" means:** CodeMap responses are roughly 10×
smaller than the source files they replace. On SampleSolution (a small
4-project test fixture), this is 90%. On production codebases, it's
typically 95-99%.

### Savings in Every Response

Every tool response includes a `meta` block with savings data. The agent
sees this automatically:

```json
{
  "meta": {
    "tokens_saved": 3200,
    "tokens_saved_total": 128000,
    "timing_ms": { "total": 12, "cache_lookup": 1, "db_query": 8 }
  }
}
```

- `tokens_saved` — estimated tokens THIS query saved vs reading raw files
- `tokens_saved_total` — cumulative savings across the entire session

You can ask the agent directly:

```
You: How many tokens has codemap saved so far in this session?

Claude: Based on the last response metadata, CodeMap has saved
approximately 128,000 tokens this session. That's roughly a 90%
reduction compared to reading source files directly.
```

### Persistent Savings File

Cumulative savings persist across sessions in `~/.codemap/_savings.json`:

```json
{
  "tokens_saved_total": 4250000,
  "cost_avoided_total": 12.75,
  "last_updated": "2026-03-06T14:23:01Z"
}
```

You can check this file anytime to see your lifetime savings. The file
updates automatically when the CodeMap daemon shuts down gracefully.

### Quick Savings Check

To see savings without leaving Claude Code:

```
You: Make any codemap query (like repo.status) and tell me the
     tokens_saved_total from the response metadata.
```

Or check the file directly:

```bash
cat ~/.codemap/_savings.json
```

---

## Next Steps

- **Read the tool examples in the README** — each tool has a usage example
- **Set up shared caching** if you work on a team
- **Try `graph.trace_feature`** — it's the most powerful tool, combining
  call graph traversal with architectural facts in one query
- **Create a `.mcp.json`** in your project root so teammates get CodeMap
  automatically

---

*CodeMap MCP — 19 tools, 90% token savings, one command to set up.*
