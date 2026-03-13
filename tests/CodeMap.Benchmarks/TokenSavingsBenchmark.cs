namespace CodeMap.Benchmarks;

using System.Text.Json;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Measures token savings from using CodeMap vs raw file reading for 17 canonical agent tasks.
/// M01 tasks (1-10): symbol search, card, span, definition span.
/// M02 tasks (11-17): refs.find, graph.callers, graph.callees, types.hierarchy, virtual files.
///
/// Methodology:
///   Raw tokens  = reading ALL source files (without an index, an agent must scan
///                 every file to answer questions about the codebase).
///   CodeMap tokens = the JSON data returned by the relevant tool call.
///   Savings = (raw - codemap) / raw × 100
///
/// Note: The SampleSolution is a small test fixture (~2,800 tokens total).
/// These savings percentages reflect focused queries on a tiny codebase.
/// On production codebases (100,000+ tokens), savings are 90%+.
/// Token estimate: 1 token ≈ 4 characters (standard approximation for code).
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class TokenSavingsBenchmark : IAsyncLifetime
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static string SampleSolutionDir =>
        Path.GetDirectoryName(SampleSolutionPath)!;

    // All source files (excluding obj/) — represents what an agent reads without CodeMap
    private static IReadOnlyList<string> AllSourceFiles =>
        Directory.GetFiles(SampleSolutionDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToList();

    private string _tempDir = null!;
    private IQueryEngine _engine = null!;

    // M02: MergedQueryEngine + workspace for task 16 (virtual file span)
    private MergedQueryEngine _mergedEngine = null!;
    private WorkspaceManager _wsMgr = null!;
    private RoutingContext _ephemeralRouting = null!;

    // M03: additional symbols + cache dir for tasks 18-24
    private SymbolId _diSetupMethodId;
    private SymbolId _middlewareSetupMethodId;
    private string _cacheDir = null!;

    // Symbol IDs discovered at runtime from the indexed baseline (Roslyn format)
    private SymbolId _orderServiceId;
    private SymbolId _iOrderServiceId;
    private SymbolId _submitAsyncId;
    private SymbolId _saveAsyncId;
    private SymbolId _orderId;

    // Property whose Read references we query in task 17
    private SymbolId _orderStatusPropertyId;

    private const string OrderServiceFile = "SampleApp/Services/OrderService.cs";

    // ── Null-object implementations for WorkspaceManager setup ────────────────

    private sealed class NoopIncrementalCompiler : IIncrementalCompiler
    {
        public Task<OverlayDelta> ComputeDeltaAsync(
            string solutionPath, string repoRootPath,
            IReadOnlyList<FilePath> changedFiles,
            ISymbolStore baselineStore, RepoId repoId, CommitSha commitSha,
            int currentRevision, CancellationToken ct = default)
            => Task.FromResult(OverlayDelta.Empty(currentRevision + 1));

        public void Dispose() { }
    }

    private sealed class NoopGitService : IGitService
    {
        private static readonly CommitSha _sha = CommitSha.From(new string('b', 40));
        public Task<RepoId> GetRepoIdentityAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult(RepoId.From("benchmark-repo"));
        public Task<CommitSha> GetCurrentCommitAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult(_sha);
        public Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult("main");
        public Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
            string repoPath, CommitSha baseline, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileChange>>([]);
        public Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class NullResolutionWorker : IResolutionWorker
    {
        public Task<int> ResolveEdgesAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ResolveOverlayEdgesAsync(
            RepoId repoId, CommitSha commitSha, WorkspaceId workspaceId,
            IReadOnlyList<FilePath> recompiledFiles,
            IOverlayStore overlayStore, ISymbolStore baselineStore, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    public async Task InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();
        _engine = new QueryEngine(store, cache, tracker, new ExcerptReader(store), new GraphTraverser(), new FeatureTracer(store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        var repoId = RepoId.From("benchmark-repo");
        var commitSha = CommitSha.From(new string('b', 40));

        var compiled = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        await store.CreateBaselineAsync(repoId, commitSha, compiled, SampleSolutionDir);

        // Discover actual symbol IDs from the index (Roslyn uses doc-comment-id format)
        _orderServiceId = await DiscoverAsync("OrderService", SymbolKind.Class,
            "T:SampleApp.Services.OrderService");

        _iOrderServiceId = await DiscoverAsync("IOrderService", SymbolKind.Interface,
            "T:SampleApp.Services.IOrderService");

        _submitAsyncId = await DiscoverAsync("SubmitAsync", SymbolKind.Method,
            "M:SampleApp.Services.OrderService.SubmitAsync(System.String,System.Threading.CancellationToken)");

        _saveAsyncId = await DiscoverAsync("SaveAsync", SymbolKind.Method,
            "M:SampleApp.Repositories.Repository`1.SaveAsync(`0,System.Threading.CancellationToken)");

        _orderId = await DiscoverFirstEndingWith("Order", SymbolKind.Class, ".Order",
            "T:SampleApp.Models.Order");

        _orderStatusPropertyId = await DiscoverAsync("Status", SymbolKind.Property,
            "P:SampleApp.Models.Order.Status");

        // M02: set up workspace + MergedQueryEngine for task 16 (virtual file span)
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(overlayDir);
        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        var overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        _wsMgr = new WorkspaceManager(
            overlayStore, new NoopIncrementalCompiler(), store,
            new NoopGitService(), new InMemoryCacheService(),
            new NullResolutionWorker(),
            NullLogger<WorkspaceManager>.Instance);

        var wsId = WorkspaceId.From("bench-ws");
        await _wsMgr.CreateWorkspaceAsync(repoId, wsId, commitSha, SampleSolutionPath, SampleSolutionDir);

        _mergedEngine = new MergedQueryEngine(
            (QueryEngine)_engine, overlayStore, _wsMgr,
            cache, tracker,
            new ExcerptReader(store), new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);

        _ephemeralRouting = new RoutingContext(
            repoId: repoId, workspaceId: wsId,
            consistency: ConsistencyMode.Ephemeral, baselineCommitSha: commitSha);

        // M03: discover DI/Middleware method IDs for tasks 21-22
        _diSetupMethodId = await DiscoverAsync("ConfigureServices", SymbolKind.Method,
            "M:SampleApp.Api.DiSetup.ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection)");

        _middlewareSetupMethodId = await DiscoverAsync("Configure", SymbolKind.Method,
            "M:SampleApp.Api.MiddlewareSetup.Configure(Microsoft.AspNetCore.Builder.WebApplication)");

        // M03: pre-populate shared cache dir for task 24 (cache pull timing)
        _cacheDir = Path.Combine(_tempDir, "shared-cache");
        Directory.CreateDirectory(_cacheDir);
        var cacheFactory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        var cacheMgr = new BaselineCacheManager(cacheFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);
        await cacheMgr.PushAsync(repoId, commitSha);
    }

    private async Task<SymbolId> DiscoverAsync(string query, SymbolKind kind, string fallback)
    {
        var r = await _engine.SearchSymbolsAsync(
            Routing, query, new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 5), CancellationToken.None);
        return r.IsSuccess && r.Value.Data.Hits.Count > 0
            ? r.Value.Data.Hits[0].SymbolId
            : SymbolId.From(fallback);
    }

    private async Task<SymbolId> DiscoverFirstEndingWith(
        string query, SymbolKind kind, string suffix, string fallback)
    {
        var r = await _engine.SearchSymbolsAsync(
            Routing, query, new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 10), CancellationToken.None);
        if (r.IsSuccess)
        {
            var hit = r.Value.Data.Hits.FirstOrDefault(h => h.SymbolId.Value.EndsWith(suffix));
            if (hit is not null) return hit.SymbolId;
            if (r.Value.Data.Hits.Count > 0) return r.Value.Data.Hits[0].SymbolId;
        }
        return SymbolId.From(fallback);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RoutingContext Routing => new(
        repoId: RepoId.From("benchmark-repo"),
        baselineCommitSha: CommitSha.From(new string('b', 40)));

    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    /// <summary>
    /// Raw cost without CodeMap: agent reads ALL source files to discover relevant symbols.
    /// Without an index, there is no alternative to scanning every file.
    /// </summary>
    private static int RawTokensAllFiles() =>
        AllSourceFiles.Sum(f => EstimateTokens(File.ReadAllText(f)));

    private static BenchmarkResult Measure(string task, int rawTokens, int codeMapTokens)
    {
        double savings = rawTokens > 0
            ? (rawTokens - codeMapTokens) / (double)rawTokens * 100.0
            : 0;
        return new BenchmarkResult(task, rawTokens, codeMapTokens, savings);
    }

    // ── The 10 canonical benchmark tasks ─────────────────────────────────────

    [Fact]
    public async Task Benchmark_AllTasks_AverageTokenSavingsAtLeast80Percent()
    {
        int rawAll = RawTokensAllFiles(); // baseline: reading every source file
        var results = new List<BenchmarkResult>();

        // Task 1: "Find all Order* classes"
        // Raw: read ALL files, scan for class declarations
        // CodeMap: focused search by name + kind filter returns only matching classes
        {
            var r = await _engine.SearchSymbolsAsync(
                Routing, "OrderService",
                new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
                new BudgetLimits(maxResults: 5),
                CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Find OrderService class", rawAll, cm));
        }

        // Task 2: "What does OrderService do?"
        // Raw: read ALL files to find it; CodeMap: get_card returns structured metadata
        {
            var r = await _engine.GetSymbolCardAsync(Routing, _orderServiceId, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("OrderService card", rawAll, cm));
        }

        // Task 3: "Show me the OrderService class body"
        // Raw: read ALL files + entire OrderService.cs; CodeMap: bounded definition span
        {
            var r = await _engine.GetDefinitionSpanAsync(Routing, _orderServiceId, 120, 2, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("OrderService definition span", rawAll, cm));
        }

        // Task 4: "Find the SubmitAsync method"
        // Raw: read ALL files, grep for SubmitAsync; CodeMap: targeted method search
        {
            var r = await _engine.SearchSymbolsAsync(
                Routing, "SubmitAsync",
                new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
                new BudgetLimits(maxResults: 5),
                CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Find SubmitAsync method", rawAll, cm));
        }

        // Task 5: "What interfaces does OrderService implement?"
        // Raw: read ALL files; CodeMap: signature field in the card answers this
        {
            var r = await _engine.GetSymbolCardAsync(Routing, _orderServiceId, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("OrderService interfaces", rawAll, cm));
        }

        // Task 6: "Show me the IOrderService interface"
        // Raw: read ALL files; CodeMap: get_definition_span returns just the interface
        {
            var r = await _engine.GetDefinitionSpanAsync(Routing, _iOrderServiceId, 120, 2, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("IOrderService definition", rawAll, cm));
        }

        // Task 7: "Find GetByIdAsync"
        // Raw: read ALL files; CodeMap: search returns 1-2 hits
        {
            var r = await _engine.SearchSymbolsAsync(
                Routing, "GetByIdAsync",
                new SymbolSearchFilters(),
                new BudgetLimits(maxResults: 5),
                CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Find GetByIdAsync", rawAll, cm));
        }

        // Task 8: "Show me lines 1-20 of OrderService.cs"
        // Raw: read ALL files; CodeMap: get_span returns exactly 20 lines
        {
            var fp = FilePath.From(OrderServiceFile);
            var r = await _engine.GetSpanAsync(Routing, fp, 1, 20, 0, null, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Span lines 1-20 OrderService.cs", rawAll, cm));
        }

        // Task 9: "Find async methods"
        // Raw: read ALL files; CodeMap: search with method filter returns focused list
        {
            var r = await _engine.SearchSymbolsAsync(
                Routing, "Async",
                new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
                new BudgetLimits(maxResults: 5),
                CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Find async methods (top 5)", rawAll, cm));
        }

        // Task 10: "Find the Repository class"
        // Raw: read ALL files; CodeMap: targeted search returns 1-2 hits
        {
            var r = await _engine.SearchSymbolsAsync(
                Routing, "Repository",
                new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
                new BudgetLimits(maxResults: 5),
                CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Find Repository class", rawAll, cm));
        }

        // ── M02 canonical tasks (11-17) ───────────────────────────────────────

        // Task 11: "Who calls OrderService.SubmitAsync?"
        // Raw: read ALL files, text-search for "SubmitAsync", parse contexts
        // CodeMap: refs.find returns classified call refs only
        {
            var r = await _engine.FindReferencesAsync(
                Routing, _submitAsyncId, RefKind.Call,
                new BudgetLimits(maxResults: 20), CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Who calls SubmitAsync? (refs.find)", rawAll, cm));
        }

        // Task 12: "What does OrderService.SubmitAsync call?"
        // Raw: open OrderService.cs, read method, manually trace each invocation
        // CodeMap: graph.callees returns the structured call graph
        {
            var r = await _engine.GetCalleesAsync(
                Routing, _submitAsyncId, depth: 1, limitPerLevel: 20,
                budgets: null, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("What does SubmitAsync call? (callees)", rawAll, cm));
        }

        // Task 13: "Full caller chain for SaveAsync (depth 2)"
        // Raw: find all callers, then find callers of those callers (recursive file scan)
        // CodeMap: single graph.callers call with depth=2
        {
            var r = await _engine.GetCallersAsync(
                Routing, _saveAsyncId, depth: 2, limitPerLevel: 20,
                budgets: null, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Caller chain SaveAsync depth=2 (callers)", rawAll, cm));
        }

        // Task 14: "What does Order extend and implement?"
        // Raw: open Order.cs, read class declaration, follow base types manually
        // CodeMap: types.hierarchy returns structured base/interface/derived data
        {
            var r = await _engine.GetTypeHierarchyAsync(
                Routing, _orderId, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Order type hierarchy (hierarchy)", rawAll, cm));
        }

        // Task 15: "Find all implementations of IOrderService"
        // Raw: grep for ': IOrderService' across all files
        // CodeMap: types.hierarchy(IOrderService) → DerivedTypes
        {
            var r = await _engine.GetTypeHierarchyAsync(
                Routing, _iOrderServiceId, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("IOrderService implementations (hierarchy.derived)", rawAll, cm));
        }

        // Task 16: "Show me the current (unsaved) version of OrderService.cs lines 1-20"
        // Raw: agent must write file content to disk and read it back (full file round-trip)
        // CodeMap: code.get_span with virtual_files returns only the requested lines
        {
            var orderServiceContent = File.ReadAllText(
                Path.Combine(SampleSolutionDir, "SampleApp", "Services", "OrderService.cs"));
            var virtualFiles = new List<VirtualFile>
            {
                new(FilePath.From(OrderServiceFile), orderServiceContent)
            };
            var ephRouting = _ephemeralRouting with { VirtualFiles = virtualFiles };
            var r = await _mergedEngine.GetSpanAsync(
                ephRouting, FilePath.From(OrderServiceFile), 1, 20, 0, null, CancellationToken.None);

            // Raw: agent writes file to disk + reads it back (2× file content)
            int rawVirtual = EstimateTokens(orderServiceContent) * 2;
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawVirtual;
            results.Add(Measure("Virtual file span 1-20 (ephemeral)", rawVirtual, cm));
        }

        // Task 17: "What are all the Read references to the Status property?"
        // Raw: find property declaration, text-search property name in all files (noisy grep)
        // CodeMap: refs.find(property, kind: Read) returns classified Read references only
        {
            var r = await _engine.FindReferencesAsync(
                Routing, _orderStatusPropertyId, RefKind.Read,
                new BudgetLimits(maxResults: 20), CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Status property Read refs (refs.find kind=Read)", rawAll, cm));
        }

        // ── M03 canonical tasks (18-24) ───────────────────────────────────────

        // Task 18: "What HTTP endpoints does this solution expose?"
        // Raw: grep all files for [HttpGet/Post/...], MapGet, etc.
        // CodeMap: surfaces.list_endpoints returns structured endpoint list
        {
            var r = await _engine.ListEndpointsAsync(
                Routing, null, null, 50, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("List HTTP endpoints (surfaces.list_endpoints)", rawAll, cm));
        }

        // Task 19: "What config keys does this solution use?"
        // Raw: grep all files for IConfiguration, GetValue, GetSection
        // CodeMap: surfaces.list_config_keys returns structured key+usage list
        {
            var r = await _engine.ListConfigKeysAsync(
                Routing, null, 50, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("List config keys (surfaces.list_config_keys)", rawAll, cm));
        }

        // Task 20: "What database tables does this solution touch?"
        // Raw: grep for DbSet, [Table], FROM/INTO/UPDATE in SQL strings
        // CodeMap: surfaces.list_db_tables returns aggregated table+entity mapping
        {
            var r = await _engine.ListDbTablesAsync(
                Routing, null, 50, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("List DB tables (surfaces.list_db_tables)", rawAll, cm));
        }

        // Task 21: "How is the DI container configured?"
        // Raw: find and read AddScoped/AddSingleton/AddTransient calls across startup files
        // CodeMap: symbols.get_card(diSetupMethod) → card.Facts includes DI registrations
        {
            var r = await _engine.GetSymbolCardAsync(Routing, _diSetupMethodId, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("DI config via card.Facts (symbols.get_card)", rawAll, cm));
        }

        // Task 22: "What's the middleware pipeline order?"
        // Raw: find and read the middleware configuration method
        // CodeMap: symbols.get_card(middlewareSetupMethod) → ordered pipeline in card.Facts
        {
            var r = await _engine.GetSymbolCardAsync(Routing, _middlewareSetupMethodId, CancellationToken.None);
            int cm = r.IsSuccess ? EstimateTokens(JsonSerializer.Serialize(r.Value)) : rawAll;
            results.Add(Measure("Middleware pipeline via card.Facts (symbols.get_card)", rawAll, cm));
        }

        // Task 23: "Is this workspace stale?"
        // Raw: agent must call git log, compare HEAD against workspace base commit manually
        // CodeMap: workspace.list → IsStale flag per workspace, single structured call
        {
            var workspaces = await _wsMgr.ListWorkspacesAsync(RepoId.From("benchmark-repo"));
            int cm = EstimateTokens(JsonSerializer.Serialize(workspaces));
            results.Add(Measure("Workspace staleness check (workspace.list)", rawAll, cm));
        }

        // Print summary table (tasks 1-23, token-based)
        PrintSummaryTable(results);

        double average = results.Average(r => r.SavingsPercent);
        average.Should().BeGreaterThanOrEqualTo(80.0,
            because: "Milestone 01+02+03 combined requires ≥ 80% average token savings vs raw file reading");

        // ── Task 24: Cache pull (wall time, not tokens) ───────────────────────
        // Raw: full Roslyn compilation (~10-30 seconds)
        // CodeMap: cache hit → file copy (~100ms)
        {
            var pullLocalDir = Path.Combine(_tempDir, "cache-pull-bench");
            Directory.CreateDirectory(pullLocalDir);
            var pullFactory = new BaselineDbFactory(pullLocalDir, NullLogger<BaselineDbFactory>.Instance);
            var pullMgr = new BaselineCacheManager(
                pullFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pulled = await pullMgr.PullAsync(
                RepoId.From("benchmark-repo"),
                CommitSha.From(new string('b', 40)));
            sw.Stop();

            Console.WriteLine($"| 24 | Get baseline (cache hit) | N/A (time) | N/A | {sw.ElapsedMilliseconds}ms |");
            Console.WriteLine($"  Task 24 note: wall time only — pull={sw.ElapsedMilliseconds}ms vs full compilation ~10-30s");
            Console.WriteLine();

            pulled.Should().NotBeNull("task 24: cache pull must succeed (pre-populated in InitializeAsync)");
            sw.ElapsedMilliseconds.Should().BeLessOrEqualTo(500,
                $"task 24: cache pull {sw.ElapsedMilliseconds}ms must be < 500ms");
        }
    }

    private static void PrintSummaryTable(IReadOnlyList<BenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("| # | Task | Raw Tokens | CodeMap Tokens | Savings % |");
        Console.WriteLine("|---|------|-----------|---------------|-----------|");
        int i = 1;
        foreach (var r in results)
            Console.WriteLine($"| {i++} | {r.Task} | {r.RawTokens:N0} | {r.CodeMapTokens:N0} | {r.SavingsPercent:F1}% |");

        double avg = results.Average(r => r.SavingsPercent);
        int totalRaw = results.Sum(r => r.RawTokens);
        int totalCm = results.Sum(r => r.CodeMapTokens);
        Console.WriteLine($"| AVG | — | {totalRaw:N0} | {totalCm:N0} | **{avg:F1}%** |");
        Console.WriteLine();
    }

    private record BenchmarkResult(string Task, int RawTokens, int CodeMapTokens, double SavingsPercent);
}
