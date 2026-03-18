# DEVELOPER-GUIDE.MD

## CodeMap — Developer Guide

This guide is for contributors who want to build, extend, debug, or test
CodeMap. It answers the "how do I..." questions you'll hit on day one.

For understanding *why* things work the way they do, see
[ARCHITECTURE-WALKTHROUGH.MD](ARCHITECTURE-WALKTHROUGH.MD).
For the full type/API contracts, see [API-SCHEMA.MD](API-SCHEMA.MD).
For architecture decisions, see [DECISIONS.MD](DECISIONS.MD).

---

## Prerequisites

- **.NET 9 SDK** — required for build and test
- **Git** — required for LibGit2Sharp integration tests
- **MSBuild** (included with .NET SDK) — required for integration tests that
  open real solutions via Roslyn MSBuildWorkspace
- A C# solution to index (or use the built-in `testdata/SampleSolution/`) — both `.sln`
  and `.slnx` formats are accepted by `index.ensure_baseline`

---

## Build & Run

```bash
git clone <repo>
cd CodeMap
dotnet build -warnaserror
dotnet run --project src/CodeMap.Daemon
```

`-warnaserror` is mandatory. The CI gate requires zero warnings. If your
change introduces a nullable warning or missing XML doc, fix it before committing.

Once running, the daemon reads JSON-RPC from stdin and writes responses to
stdout. Logs go to stderr and `~/.codemap/logs/codemap-{date}.log`.

---

## Run Tests

```bash
# Fast unit tests (~5-10s, no MSBuild)
dotnet test --filter "Category!=Integration&Category!=Benchmark"

# Integration tests (~30-60s, requires MSBuild + SampleSolution)
dotnet test --filter "Category=Integration"

# Token savings benchmark (xUnit, validates >=80% savings across 24 tasks)
dotnet test --filter "Category=Benchmark" -v normal

# BenchmarkDotNet microbenchmarks (Release mode, run from repo root)
dotnet run --project tests/CodeMap.Benchmarks -c Release
```

Run the fast suite constantly during development. Run integration tests
before committing. BenchmarkDotNet is for performance regression checks —
run it when you change query paths or storage code.

---

## Debug Locally

The MCP server communicates over stdin/stdout using JSON-RPC 2.0. To send
a request manually:

1. Start the daemon: `dotnet run --project src/CodeMap.Daemon`
2. Paste a JSON-RPC message to stdin (one line, no trailing newline needed):

```json
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"repo.status","arguments":{"repo_path":"/path/to/your/repo"}}}
```

3. The JSON response appears on stdout.
4. Diagnostic logs appear on stderr.

For structured log output, check `~/.codemap/logs/`. Each line is a JSON
object with `timestamp`, `level`, `message`, and structured properties.

To enable verbose logging, set `log_level` in `~/.codemap/config.json`:

```json
{ "log_level": "Debug" }
```

---

## Project Structure

CodeMap has 17 projects total: 7 source projects, 9 test projects, and 1
shared test utilities project.

```
src/
  CodeMap.Core        — Domain types, interfaces, enums, errors. Zero dependencies.
  CodeMap.Git         — Git integration via LibGit2Sharp (repo identity, diffs, branch state).
  CodeMap.Roslyn      — Roslyn compilation, symbol/ref/fact/type-relation extraction.
  CodeMap.Storage     — SQLite persistence for baselines, overlays, and the shared cache.
  CodeMap.Query       — Query engine, L1 cache, workspace manager, MergedQueryEngine.
  CodeMap.Mcp         — MCP tool handlers (JSON-RPC dispatch, parameter parsing, routing).
  CodeMap.Daemon      — DI composition root, entry point, config loading, file logging.

tests/
  CodeMap.Core.Tests         — Core model/interface contract tests (types, validation).
  CodeMap.Git.Tests          — Git operation tests (real temp repos via LibGit2Sharp).
  CodeMap.Roslyn.Tests       — Extractor unit tests + Roslyn integration tests.
  CodeMap.Storage.Tests      — SQLite store tests with real temp databases.
  CodeMap.Query.Tests        — Query engine + workspace manager tests (mocked stores).
  CodeMap.Mcp.Tests          — MCP handler delegation tests (mocked query engine).
  CodeMap.Daemon.Tests       — Config loading + file logging tests.
  CodeMap.Integration.Tests  — Cross-layer E2E workflows (MCP → Roslyn → DB).
  CodeMap.Benchmarks         — Token savings + BenchmarkDotNet performance suites.
  CodeMap.TestUtilities      — Shared builders, fixtures, test helpers (no xUnit ref).

testdata/
  SampleSolution/     — 4-project .NET solution used in integration/benchmark tests.
                        Projects: SampleApp, SampleApp.Tests, SampleApp.Shared, SampleApp.Api.

docs/
  MILESTONE.MD               — Project overview, all phases and milestones.
  API-SCHEMA.MD              — All type definitions and MCP tool contracts.
  SYSTEM-ARCHITECTURE.MD     — Component design, DB schema, query model.
  DECISIONS.MD               — Architecture Decision Records (append-only).
  PERFORMANCE-BASELINE.MD    — BenchmarkDotNet baseline measurements.
  DEVELOPER-GUIDE.MD         — This file.
  ARCHITECTURE-WALKTHROUGH.MD — Narrative request trace through all layers.
  PHASE-MM-NN.MD             — Task specs for each implementation phase.
```

---

## Dependency Rules

The dependency graph is strict and enforced as build errors:

```
Core ← Git, Roslyn, Storage, Query, Mcp, Daemon
Git  ← Core (+ LibGit2Sharp)
Roslyn ← Core (+ Roslyn 4.x, MSBuildWorkspace)
Storage ← Core (+ Microsoft.Data.Sqlite)
Query ← Core (NOT Storage — queries through ISymbolStore/IOverlayStore interfaces)
Mcp ← Core, Query
Daemon ← ALL (the only project that references everything)
```

**Why these rules exist:**

- **Core has zero deps** so types can be shared anywhere without pulling
  in heavy libraries. Roslyn.dll would bloat Storage tests; SQLite would
  bloat Roslyn tests.

- **Query talks to Storage through interfaces** (`ISymbolStore`, `IOverlayStore`)
  defined in Core. This means Query tests can mock the store without
  SQLite, and you can swap in a different storage backend without touching
  the query layer.

- **Daemon is the sole composition root.** It's the only project that knows
  about every layer. All DI wiring happens in
  [`ServiceRegistration.cs`](../src/CodeMap.Daemon/ServiceRegistration.cs).

- **TestUtilities has no xUnit reference** to prevent it from being treated
  as a test assembly by the test runner. It only contains builders, fixtures,
  and helpers that any test project can reference.

---

## How to Add a New MCP Tool

This recipe adds a hypothetical `symbols.count` tool that returns symbol
counts by kind.

### Step 1: Define the response type in Core

Create a record in `src/CodeMap.Core/Models/`:

```csharp
// src/CodeMap.Core/Models/SymbolCountResponse.cs
namespace CodeMap.Core.Models;

/// <summary>Symbol count grouped by kind.</summary>
public record SymbolCountResponse(
    int Total,
    IReadOnlyDictionary<string, int> ByKind);
```

### Step 2: Add the query method to IQueryEngine

Open [`src/CodeMap.Core/Interfaces/IQueryEngine.cs`](../src/CodeMap.Core/Interfaces/IQueryEngine.cs)
and add:

```csharp
/// <summary>Returns symbol counts grouped by kind for the given routing context.</summary>
Task<Result<ResponseEnvelope<SymbolCountResponse>, CodeMapError>>
    CountSymbolsAsync(RoutingContext routing, CancellationToken ct);
```

Update the interface contract test in
`tests/CodeMap.Core.Tests/` — the test that asserts `IQueryEngine` has
exactly N methods must be incremented by 1.

### Step 3: Implement in QueryEngine

Open [`src/CodeMap.Query/QueryEngine.cs`](../src/CodeMap.Query/QueryEngine.cs)
and add the implementation. Use `TimingContext` to record per-phase timing:

```csharp
public async Task<Result<ResponseEnvelope<SymbolCountResponse>, CodeMapError>>
    CountSymbolsAsync(RoutingContext routing, CancellationToken ct)
{
    using var timing = new TimingContext();
    var cacheKey = $"count:{routing.RepoId}:{routing.BaselineCommitSha}";

    // Check L1 cache
    timing.EndCacheLookup();
    if (_cache.TryGet(cacheKey, out SymbolCountResponse? cached))
        return Ok(cached!, timing, 0);

    // Query storage
    var counts = await _store.CountSymbolsByKindAsync(routing.RepoId,
        routing.BaselineCommitSha, ct).ConfigureAwait(false);
    timing.EndDbQuery();

    var response = new SymbolCountResponse(counts.Values.Sum(), counts);
    _cache.Set(cacheKey, response);

    var answer = $"Found {response.Total} symbols.";
    return Ok(response, timing, EstimateTokensSaved(response.Total));
}
```

### Step 4: Implement in MergedQueryEngine

Open [`src/CodeMap.Query/MergedQueryEngine.cs`](../src/CodeMap.Query/MergedQueryEngine.cs).
For most read tools, the workspace merge is: query both overlay and baseline,
then combine. For a count tool, decide whether overlay wins entirely or you
sum both. Add `<inheritdoc/>` plus a `<remarks>` describing the merge strategy.

### Step 5: Create the MCP handler

Create `src/CodeMap.Mcp/Handlers/SymbolCountHandler.cs`:

```csharp
namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;

/// <summary>Handles the <c>symbols.count</c> MCP tool.</summary>
/// <remarks>
/// <b>JSON params:</b> repo_path (required); workspace_id (optional).
/// Returns INVALID_ARGUMENT if repo_path is missing.
/// </remarks>
public sealed class SymbolCountHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;

    public SymbolCountHandler(IQueryEngine queryEngine, IGitService gitService)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
    }

    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "symbols.count",
            "Return the count of indexed symbols grouped by kind.",
            BuildSchema(required: ["repo_path"], properties: new JsonObject
            {
                ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                ["workspace_id"] = Prop("string", "Optional: workspace ID"),
            }),
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args);

        var result = await _queryEngine.CountSymbolsAsync(routing, ct).ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    // ... Helpers (BuildRouting, Ok, Err, InvalidArg, BuildSchema, Prop)
    // Copy the helper block from any existing handler — it's identical.
}
```

### Step 6: Register in ServiceRegistration.cs

Open [`src/CodeMap.Daemon/ServiceRegistration.cs`](../src/CodeMap.Daemon/ServiceRegistration.cs):

1. Add `services.AddSingleton<SymbolCountHandler>();` in `AddCodeMapServices`.
2. Add `sp.GetRequiredService<SymbolCountHandler>().Register(registry);` in `RegisterMcpTools`.
3. Update the tool-count comment from N to N+1.

### Step 7: Write tests

- **Unit test** (`tests/CodeMap.Mcp.Tests/`): Verify handler calls
  `_queryEngine.CountSymbolsAsync` with correct routing. Use NSubstitute.
- **Unit test** (`tests/CodeMap.Query.Tests/`): Verify `QueryEngine.CountSymbolsAsync`
  calls `_store.CountSymbolsByKindAsync` and caches the result.
- **Integration test** (`tests/CodeMap.Integration.Tests/`): Index
  SampleSolution, call the tool, assert count > 0.
- **Contract test** (`tests/CodeMap.Core.Tests/`): Update IQueryEngine method count.

---

## Composite Tools (No New Storage Methods)

`symbols.get_context` is an example of a **composite tool** — it orchestrates
existing `IQueryEngine` methods rather than adding new storage queries:

1. `GetSymbolCardAsync` → primary card
2. `GetCalleesAsync` → callee list
3. `GetSymbolCardAsync` × N → callee cards
4. `GetDefinitionSpanAsync` × (1 + N) → source code for each

No new `ISymbolStore` methods were needed. The value of `symbols.get_context`
comes from **composing existing operations**, not from new data access.

### When to Use the Composite Pattern

- Your tool's value is in **combining or summarizing existing data**
- No new information needs to be extracted from source code or stored in SQLite
- The composition logic belongs in `CodeMap.Query` (not `CodeMap.Mcp`)

### How to Implement

1. Create a static helper class in `CodeMap.Query/` (e.g., `ContextBuilder.cs`)
   with `internal static` methods. Add `InternalsVisibleTo` if tests need access.
2. Add the `IQueryEngine` method to `CodeMap.Core/Interfaces/IQueryEngine.cs`.
3. Implement in both `QueryEngine.cs` and `MergedQueryEngine.cs`.
4. Create the MCP handler in `CodeMap.Mcp/Handlers/` following the existing pattern.

Compare to the **standard recipe** (above) which requires:
- New response type in Core
- New `IQueryEngine` method
- New `ISymbolStore` method + SQL in BaselineStore + OverlayStore

A composite tool skips the storage layer entirely.

---

## How to Add a New Fact Extractor

This recipe adds a hypothetical `TestCoverage` fact kind that records
which methods have test coverage attributes.

### Step 1: Add the FactKind enum value

Open [`src/CodeMap.Core/Enums/FactKind.cs`](../src/CodeMap.Core/Enums/FactKind.cs)
and add:

```csharp
TestCoverage = 7,  // Keep values in ascending order
```

### Step 2: Create the extractor

Create `src/CodeMap.Roslyn/Extraction/TestCoverageExtractor.cs`:

```csharp
namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Extracts test coverage annotations from methods with [Fact], [Test], or [Theory].
/// </summary>
internal static class TestCoverageExtractor
{
    private static readonly HashSet<string> TestAttributes =
        new(StringComparer.Ordinal) { "Fact", "Test", "Theory", "TestMethod" };

    public static IReadOnlyList<ExtractedFact> ExtractAll(
        Compilation compilation,
        string solutionDir,
        Dictionary<SymbolId, StableId>? stableIdMap = null)
    {
        var facts = new List<ExtractedFact>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var hasTestAttr = method.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Any(attr => TestAttributes.Contains(attr.Name.ToString()));
                if (!hasTestAttr) continue;

                var symbol = model.GetDeclaredSymbol(method);
                if (symbol is null) continue;

                var symbolId = SymbolId.From(symbol.GetDocumentationCommentId() ?? "");
                if (symbolId.IsEmpty) continue;

                stableIdMap?.TryGetValue(symbolId, out var stableId);

                var filePath = FilePath.FromAbsolute(tree.FilePath, solutionDir);
                var lineSpan = tree.GetLineSpan(method.Span);

                facts.Add(new ExtractedFact(
                    symbolId,
                    stableId,
                    FactKind.TestCoverage,
                    Value: "covered",         // The value format — keep it simple
                    filePath,
                    LineStart: lineSpan.StartLinePosition.Line + 1,
                    LineEnd: lineSpan.EndLinePosition.Line + 1,
                    Confidence.High));
            }
        }

        return facts;
    }
}
```

### Step 3: Choose a Value format

Convention: `display_value|metadata` (pipe-separated). The first segment
before `|` is stripped by `ParseDisplayValue` in FeatureTracer for clean
display. Examples from existing extractors:

| Fact Kind        | Value format                          |
|------------------|---------------------------------------|
| Route            | `"GET /api/orders"`                   |
| Config           | `"App:MaxRetries|GetValue"`           |
| DbTable          | `"Orders|DbSet<Order>"`               |
| DiRegistration   | `"IOrderSvc → OrderSvc|Scoped"`       |
| Middleware       | `"UseAuthentication|pos:2"`           |
| RetryPolicy      | `"RetryAsync(3)|Polly"`              |

For `TestCoverage`, `"covered"` is fine — no pipe needed.

### Step 4: Wire into the compilation pipeline

Open [`src/CodeMap.Roslyn/RoslynCompiler.cs`](../src/CodeMap.Roslyn/RoslynCompiler.cs)
and add the call in the fact extraction section:

```csharp
allFacts.AddRange(TestCoverageExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
```

Open [`src/CodeMap.Roslyn/IncrementalCompiler.cs`](../src/CodeMap.Roslyn/IncrementalCompiler.cs)
and add the same line in the equivalent fact extraction block.

### Step 5: Write unit tests

Create `tests/CodeMap.Roslyn.Tests/Extraction/TestCoverageExtractorTests.cs`.
Use in-memory Roslyn compilation — no MSBuild needed for extraction unit tests:

```csharp
[Fact]
public void ExtractAll_MethodWithFactAttribute_ReturnsCoverageFact()
{
    var code = """
        public class MyTests
        {
            [Fact]
            public void MyTest() { }
        }
        """;

    var tree = CSharpSyntaxTree.ParseText(code);
    var compilation = CSharpCompilation.Create("Test",
        [tree],
        references: Basic.Reference.Assemblies.Net90.References.All,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var facts = TestCoverageExtractor.ExtractAll(compilation, solutionDir: "/");

    facts.Should().ContainSingle(f => f.Kind == FactKind.TestCoverage);
}
```

### Step 6: Facts automatically appear on SymbolCard

No additional wiring needed. `QueryEngine.GetSymbolCardAsync` calls
`_store.GetFactsForSymbolAsync`, which queries the `facts` table for all
rows matching the symbol. Your `TestCoverage` facts will appear in
`SymbolCard.Facts` automatically.

---

## How to Add a Storage Method

This recipe adds a hypothetical `CountSymbolsByKindAsync` method.

### Step 1: Define the interface in Core

Open [`src/CodeMap.Core/Interfaces/ISymbolStore.cs`](../src/CodeMap.Core/Interfaces/ISymbolStore.cs):

```csharp
/// <summary>Returns a count of symbols grouped by their kind string.</summary>
Task<IReadOnlyDictionary<string, int>> CountSymbolsByKindAsync(
    RepoId repoId, CommitSha commitSha, CancellationToken ct);
```

Update the method-count comment (`ISymbolStore has N methods`) and the
contract test that asserts the method count.

### Step 2: Implement in BaselineStore

Open [`src/CodeMap.Storage/BaselineStore.cs`](../src/CodeMap.Storage/BaselineStore.cs):

```csharp
public async Task<IReadOnlyDictionary<string, int>> CountSymbolsByKindAsync(
    RepoId repoId, CommitSha commitSha, CancellationToken ct)
{
    var db = await _dbFactory.GetOrCreateAsync(repoId, commitSha, ct).ConfigureAwait(false);
    const string sql = "SELECT symbol_kind, COUNT(*) FROM symbols GROUP BY symbol_kind";

    await using var conn = db.OpenConnection();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    var result = new Dictionary<string, int>();
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
        result[reader.GetString(0)] = reader.GetInt32(1);

    return result;
}
```

### Step 3: Add `<inheritdoc/>` to the implementation

Since the interface has the full doc comment, just add `/// <inheritdoc/>`
above the method in `BaselineStore`. The build enforces this — missing XML
docs are warnings-as-errors.

### Step 4: Write a storage test

Create or add to `tests/CodeMap.Storage.Tests/BaselineStoreTests.cs`:

```csharp
[Fact]
public async Task CountSymbolsByKindAsync_WithMixedSymbols_ReturnsGroupedCounts()
{
    // Arrange — use a real temp database (helper from TestUtilities)
    using var fixture = new BaselineDbFixture();
    await fixture.SeedWithSampleSymbolsAsync();

    // Act
    var counts = await fixture.Store.CountSymbolsByKindAsync(
        fixture.RepoId, fixture.CommitSha, CancellationToken.None);

    // Assert
    counts.Should().ContainKey("Class").WhoseValue.BePositive();
}
```

Storage tests use real SQLite temp directories. The `BaselineDbFixture`
helper (in `CodeMap.TestUtilities`) creates a temp dir and disposes it via
`IDisposable`. On Windows, call `SqliteConnection.ClearAllPools()` in
`Dispose()` before deleting the temp dir to avoid file-lock errors.

---

## How to Write Tests

### Unit Tests — mock everything

Use NSubstitute for interface mocking. One test class per production class.
Naming convention: `MethodName_Scenario_ExpectedResult`.

```csharp
// Example: tests/CodeMap.Mcp.Tests/Handlers/SymbolCountHandlerTests.cs
public class SymbolCountHandlerTests
{
    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _gitService = Substitute.For<IGitService>();
    private readonly SymbolCountHandler _handler;

    public SymbolCountHandlerTests()
    {
        _gitService.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestConstants.RepoId);
        _gitService.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestConstants.CommitSha);
        _handler = new SymbolCountHandler(_queryEngine, _gitService);
    }

    [Fact]
    public async Task HandleAsync_WithValidRepoPath_CallsQueryEngine()
    {
        // Arrange
        var args = new JsonObject { ["repo_path"] = "/some/repo" };
        _queryEngine.CountSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(/* ... */));

        // Act
        var result = await _handler.HandleAsync(args, CancellationToken.None);

        // Assert
        await _queryEngine.Received(1).CountSymbolsAsync(
            Arg.Any<RoutingContext>(), Arg.Any<CancellationToken>());
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_MissingRepoPath_ReturnsInvalidArgument()
    {
        var result = await _handler.HandleAsync(args: null, CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("repo_path");
    }
}
```

### Integration Tests — real implementations

Use `IndexedSampleSolutionFixture` for a shared, pre-indexed SampleSolution.
Tag every integration test:

```csharp
[Trait("Category", "Integration")]
public class SymbolCountIntegrationTests(IndexedSampleSolutionFixture f)
    : IClassFixture<IndexedSampleSolutionFixture>
{
    [Fact]
    public async Task CountSymbolsAsync_IndexedSolution_ReturnsPositiveCounts()
    {
        var routing = new RoutingContext(f.RepoId, f.CommitSha);
        var result = await f.QueryEngine.CountSymbolsAsync(routing, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Total.Should().BeGreaterThan(0);
    }
}
```

### Storage Tests — real SQLite temp directories

```csharp
public class BaselineStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly BaselineDbFactory _factory;
    private readonly BaselineStore _store;

    public BaselineStoreTests()
    {
        _factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(_factory, NullLogger<BaselineStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();  // Required on Windows before deleting
        Directory.Delete(_tempDir, recursive: true);
    }
}
```

### Token Savings Benchmark (xUnit)

The savings benchmark in `CodeMap.Benchmarks` verifies that the 24-task
benchmark suite achieves ≥80% average token savings. Run with:

```bash
dotnet test --filter "Category=Benchmark" -v normal
```

### BenchmarkDotNet Microbenchmarks

Run with `dotnet run --project tests/CodeMap.Benchmarks -c Release`. Never
tag a BenchmarkDotNet `[Benchmark]` method with xUnit `[Fact]` — the test
runner will invoke it without warmup and produce garbage numbers.

---

## Configuration Reference

Configuration lives at `~/.codemap/config.json`. It is loaded once at
startup; changes require a daemon restart.

```json
{
  "log_level": "Information",
  "shared_cache_dir": "/mnt/team-cache/codemap",
  "budget_overrides": {
    "max_results": 100,
    "max_lines": 500,
    "max_chars": 50000
  }
}
```

| Field                        | Default       | Description                                                         |
|------------------------------|---------------|---------------------------------------------------------------------|
| `log_level`                  | `Information` | Minimum log level: `Trace`, `Debug`, `Information`, `Warning`, `Error` |
| `shared_cache_dir`           | `null`        | Directory for the shared baseline cache (team-shared file store). Null = cache disabled. |
| `budget_overrides.max_results` | `null`      | Override the hard cap on result counts (e.g., for search/refs).     |
| `budget_overrides.max_lines` | `null`        | Override the hard cap on excerpt line count.                        |
| `budget_overrides.max_chars` | `null`        | Override the hard cap on response character count.                  |

**Environment variable override:** `CODEMAP_CACHE_DIR` overrides
`shared_cache_dir` from config.json. Useful for CI without modifying
the config file:

```bash
export CODEMAP_CACHE_DIR=/mnt/team-cache/codemap
dotnet run --project src/CodeMap.Daemon
```

**Log files** are written to `~/.codemap/logs/codemap-{yyyy-MM-dd}.log`
with daily rotation. Each line is a JSON object:

```json
{"timestamp":"2026-03-06T10:23:45.123Z","level":"Information","message":"Baseline indexed","repoId":"abc123...","symbolCount":4521}
```

---

## When to Use Each Code-Reading Tool

Four tools read source code. The right choice depends on what else you need.

### `symbols.get_card` — default choice

Returns metadata, facts, **and** source code in one call. Auto-includes the
full source for the symbol (up to 100 lines, `include_code: false` to skip).

Use when you want to understand a symbol: its signature, documentation,
DI registrations, config keys used, exceptions thrown, and its implementation.

```
symbols.get_card { symbol_id: "T:MyApp.OrderService" }
→ kind, signature, docs, facts, source code
```

### `symbols.get_definition_span` — batch reads or code-only

Returns only the source code, with no card DB query or fact hydration.
Saves a small amount of overhead per call.

Use when:
- You are looping over many symbols and only need their code (batch reads)
- You want precise line-range control without the card response structure
- You already have a card and just want to refresh the code

```
symbols.get_definition_span { symbol_id: "T:MyApp.OrderService" }
→ file_path, span_start, span_end, source_lines[]
```

### `code.get_span` — arbitrary line ranges

Reads any line range from any file. Not symbol-aware — it reads raw lines.

Use when:
- You need lines that don't correspond to a symbol boundary (config files,
  migration scripts, arbitrary context around a line number)
- You need more than 100 lines of a large symbol
- You want to read a file that isn't indexed (e.g., a `.md` or `.json` file)

```
code.get_span { file_path: "src/Program.cs", start_line: 1, end_line: 50 }
→ source_lines[]
```

### `code.search_text` — grep across indexed source files

Searches file content by regex or substring. Returns file path, line number,
and a one-line excerpt for each match. Restricted to files in the baseline index
(already filtered to source files, no bin/obj).

Use when:
- You need to find a specific string literal, error message, or TODO comment
- You want all files that reference a particular config key or constant
- You need a quick cross-file search without knowing which symbol contains the text

```
code.search_text { repo_path: "...", pattern: "OrderNotFoundException", limit: 50 }
→ matches: [{ file_path, line, excerpt }, ...]
```

Supports `file_path` filter to restrict to a subdirectory (e.g., `"src/"`) and
`limit` up to 200 (default 50).

---

## Common Pitfalls

These are the constraints that catch contributors most often. They're all
documented in CLAUDE.MD and DECISIONS.MD, but consolidated here for quick
reference.

### FTS5 bare `*` wildcard doesn't work

SQLite FTS5 does not support a bare `*` as a match-all pattern. Use a
prefix like `Order*`, or use `ISymbolStore.GetSymbolsByFileAsync` for
file-based queries that need all symbols. (ADR-017)

```csharp
// WRONG — throws or returns nothing:
_store.SearchSymbolsAsync(repoId, sha, "*", kinds: null, limit: 100, ct);

// RIGHT — use a real prefix:
_store.SearchSymbolsAsync(repoId, sha, "Order*", kinds: null, limit: 100, ct);
```

### stdout is sacred — never write to it

The daemon communicates JSON-RPC over stdout. Any stray `Console.Write` or
`Console.WriteLine` corrupts the protocol stream and breaks MCP clients.
Always use `ILogger<T>` (goes to stderr + log file). Never `Console.Write`.

### MSBuildWorkspace needs the SDK at runtime

`MSBuildWorkspace.Create()` requires MSBuild binaries at runtime, not just
at build time. If you build a self-contained binary or Docker image, use the
SDK base image (not the runtime-only image). The published Dockerfile uses
`mcr.microsoft.com/dotnet/sdk:9.0` for this reason.

### No PublishTrimmed — Roslyn uses reflection

Roslyn loads analyzers and workspace hosts via reflection. Publishing with
`-p:PublishTrimmed=true` silently drops the reflection targets and produces
a binary that crashes on `MSBuildWorkspace.Create()`. The csproj conditions
`PublishSingleFile` on the RID to avoid the NETSDK1097 error during `dotnet pack`.

### Overlay merge strategy differs per tool

`MergedQueryEngine` has 13 methods, each with a different overlay/baseline
merge strategy. Read the `<remarks>` on each method before assuming a simple
"overlay wins" rule applies. For example:

- `SearchSymbolsAsync` — file-authoritative (baseline symbols from
  overlay-reindexed files are excluded)
- `ListDbTablesAsync` — table-name-authoritative (overlay tables supersede
  same-named baseline tables, regardless of file)
- `TraceFeatureAsync` — baseline BFS tree + per-node overlay fact enrichment

### stable_id columns are nullable — old baselines don't have them

The `stable_id` column was added in Milestone 03. Baselines created before
that are missing it. `MergedQueryEngine` falls back to FQN-based merging
when `stable_id` is null. Don't assume `stable_id` is always populated.

### CommitSha must be exactly 40 lowercase hex chars

`CommitSha` validates format in its constructor. Passing a short SHA,
uppercase hex, or branch name throws `ArgumentException`. Always resolve
to a full 40-char SHA before constructing a `CommitSha`.

### Package versions live only in Directory.Packages.props

Central Package Management is enabled. Adding a `<PackageReference Version="...">`
in a `.csproj` file is a build error. Versions go in `Directory.Packages.props`;
`.csproj` files only declare `<PackageReference Include="..." />`.

### TestUtilities must not reference xUnit

`CodeMap.TestUtilities` is referenced by both test projects and
`CodeMap.Integration.Tests`. If it references xUnit, the test runner
discovers it as a test assembly, tries to run it, and fails. Only add
packages to TestUtilities that are needed for builders and helpers.
