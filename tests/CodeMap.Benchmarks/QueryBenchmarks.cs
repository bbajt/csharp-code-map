namespace CodeMap.Benchmarks;

using BenchmarkDotNet.Attributes;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// BenchmarkDotNet benchmarks for all query-path tools (PHASE-04-01 T01).
///
/// Setup: indexes SampleSolution once via real Roslyn. All benchmarks run
/// against the populated baseline — GlobalSetup time is NOT included.
///
/// Run: dotnet run --project tests/CodeMap.Benchmarks -c Release -- --filter "QueryBenchmarks*"
///
/// Performance targets from SYSTEM-ARCHITECTURE.MD Section 12:
///   symbols.search        p95 &lt; 30 ms
///   symbols.get_card      p95 &lt; 10 ms
///   get_card + facts      p95 &lt; 20 ms
///   refs.find             p95 &lt; 80 ms
///   graph.callers/callees p95 &lt; 150 ms
///   types.hierarchy       p95 &lt; 30 ms
///   surfaces.*            p95 &lt; 50 ms
///   workspace.list        p95 &lt; 20 ms
/// </summary>
[Config(typeof(CodeMapBenchmarkConfig))]
[MemoryDiagnoser]
public class QueryBenchmarks
{
    // BenchmarkDotNet runs each benchmark in a subprocess from a temp dir, so we
    // can't use a fixed number of ".." traversals. Walk up the tree instead.
    private static string SampleSolutionPath => FindSolutionFile();
    private static string SampleSolutionDir => Path.GetDirectoryName(SampleSolutionPath)!;

    private static string FindSolutionFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "SampleSolution", "SampleSolution.sln");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fallback: fixed relative path (works for xUnit in-process run)
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));
    }

    private string _tempDir = null!;
    private IQueryEngine _engine = null!;
    private RoutingContext _routing = default!;
    private RepoId _repoId;
    private SymbolId _knownSymbolId;   // OrderService class
    private SymbolId _knownMethodId;   // SubmitAsync method
    private SymbolId _knownTypeId;     // Order type (for hierarchy)
    private SymbolId _diMethodId;      // ConfigureServices (has DiRegistration facts)
    private StableId _knownStableId;   // stable id for GetSymbolByStableId bench
    private bool _stableIdValid;
    private WorkspaceManager _wsMgr = null!;

    // ── Null-object implementations ──────────────────────────────────────────

    private sealed class NoopGitService : IGitService
    {
        private readonly CommitSha _sha;
        public NoopGitService(CommitSha sha) => _sha = sha;

        public Task<RepoId> GetRepoIdentityAsync(string r, CancellationToken ct) => Task.FromResult(RepoId.From("qbench-repo"));
        public Task<CommitSha> GetCurrentCommitAsync(string r, CancellationToken ct) => Task.FromResult(_sha);
        public Task<string> GetCurrentBranchAsync(string r, CancellationToken ct) => Task.FromResult("main");
        public Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(string r, CommitSha b, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FileChange>>([]);
        public Task<bool> IsCleanAsync(string r, CancellationToken ct) => Task.FromResult(true);
        public Task<CommitSha?> ResolveCommitAsync(string r, string c, CancellationToken ct) => Task.FromResult<CommitSha?>(null);
    }

    private sealed class NoopIncrementalCompiler : IIncrementalCompiler
    {
        public Task<OverlayDelta> ComputeDeltaAsync(
            string sp, string rr, IReadOnlyList<FilePath> files,
            ISymbolStore store, RepoId r, CommitSha sha, int rev, CancellationToken ct)
            => Task.FromResult(OverlayDelta.Empty(rev + 1));

        public void Dispose() { }
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

    // ── Setup / Cleanup ──────────────────────────────────────────────────────

    [GlobalSetup]
    public async Task Setup()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-qbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _repoId = RepoId.From("qbench-repo");
        var commitSha = CommitSha.From(new string('c', 40));

        var store = new CustomSymbolStore(_tempDir);
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        _engine = new QueryEngine(
            store, cache, tracker,
            new ExcerptReader(store), new GraphTraverser(),
            new FeatureTracer(store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var compiled = await compiler.CompileAndExtractAsync(SampleSolutionPath).ConfigureAwait(false);
        await store.CreateBaselineAsync(_repoId, commitSha, compiled, SampleSolutionDir).ConfigureAwait(false);

        _routing = new RoutingContext(repoId: _repoId, baselineCommitSha: commitSha);

        // Discover symbol IDs for benchmark parameters
        _knownSymbolId = await DiscoverAsync("OrderService", SymbolKind.Class, "T:SampleApp.Services.OrderService").ConfigureAwait(false);
        _knownMethodId = await DiscoverAsync("SubmitAsync", SymbolKind.Method, "M:SampleApp.Services.OrderService.SubmitAsync").ConfigureAwait(false);
        _knownTypeId = await DiscoverEndingWithAsync("Order", SymbolKind.Class, ".Order", "T:SampleApp.Models.Order").ConfigureAwait(false);
        _diMethodId = await DiscoverAsync("ConfigureServices", SymbolKind.Method, "M:SampleApp.Api.DiSetup.ConfigureServices").ConfigureAwait(false);

        // Discover stable ID from OrderService card
        var cardResult = await _engine.GetSymbolCardAsync(_routing, _knownSymbolId).ConfigureAwait(false);
        if (cardResult.IsSuccess && cardResult.Value.Data.StableId.HasValue)
        {
            _knownStableId = cardResult.Value.Data.StableId.Value;
            _stableIdValid = true;
        }

        // WorkspaceManager for ListWorkspaces benchmark
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(overlayDir);
        var overlayStore = new CustomEngineOverlayStore(store, _tempDir);
        _wsMgr = new WorkspaceManager(
            overlayStore, new NoopIncrementalCompiler(), store,
            new NoopGitService(commitSha), cache,
            new NullResolutionWorker(),
            NullLogger<WorkspaceManager>.Instance);

        await _wsMgr.CreateWorkspaceAsync(
            _repoId, WorkspaceId.From("bench-ws"),
            commitSha, SampleSolutionPath, SampleSolutionDir).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Discover helpers ─────────────────────────────────────────────────────

    private async Task<SymbolId> DiscoverAsync(string query, SymbolKind kind, string fallback)
    {
        var r = await _engine.SearchSymbolsAsync(
            _routing, query,
            new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 5)).ConfigureAwait(false);
        return r.IsSuccess && r.Value.Data.Hits.Count > 0
            ? r.Value.Data.Hits[0].SymbolId
            : SymbolId.From(fallback);
    }

    private async Task<SymbolId> DiscoverEndingWithAsync(string query, SymbolKind kind, string suffix, string fallback)
    {
        var r = await _engine.SearchSymbolsAsync(
            _routing, query,
            new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 10)).ConfigureAwait(false);
        if (r.IsSuccess)
        {
            var hit = r.Value.Data.Hits.FirstOrDefault(h => h.SymbolId.Value.EndsWith(suffix));
            if (hit is not null) return hit.SymbolId;
            if (r.Value.Data.Hits.Count > 0) return r.Value.Data.Hits[0].SymbolId;
        }
        return SymbolId.From(fallback);
    }

    // ── Benchmark methods ────────────────────────────────────────────────────
    // Return object to prevent dead code elimination.

    /// <summary>symbols.search — FTS query, limit=20. Target: p95 &lt; 30ms</summary>
    [Benchmark]
    public async Task<object> SearchSymbols()
        => await _engine.SearchSymbolsAsync(
            _routing, "OrderService",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 20));

    /// <summary>symbols.get_card (no facts). Target: p95 &lt; 10ms</summary>
    [Benchmark]
    public async Task<object> GetSymbolCard()
        => await _engine.GetSymbolCardAsync(_routing, _knownSymbolId);

    /// <summary>symbols.get_card on a method with facts. Target: p95 &lt; 20ms</summary>
    [Benchmark]
    public async Task<object> GetSymbolCardWithFacts()
        => await _engine.GetSymbolCardAsync(_routing, _diMethodId);

    /// <summary>refs.find — all refs to a method, limit=50. Target: p95 &lt; 80ms</summary>
    [Benchmark]
    public async Task<object> FindReferences()
        => await _engine.FindReferencesAsync(
            _routing, _knownMethodId,
            null, new BudgetLimits(maxResults: 50));

    /// <summary>graph.callers depth=2. Target: p95 &lt; 150ms</summary>
    [Benchmark]
    public async Task<object> GetCallers()
        => await _engine.GetCallersAsync(
            _routing, _knownMethodId,
            depth: 2, limitPerLevel: 20, budgets: null);

    /// <summary>graph.callees depth=2. Target: p95 &lt; 150ms</summary>
    [Benchmark]
    public async Task<object> GetCallees()
        => await _engine.GetCalleesAsync(
            _routing, _knownMethodId,
            depth: 2, limitPerLevel: 20, budgets: null);

    /// <summary>types.hierarchy. Target: p95 &lt; 30ms</summary>
    [Benchmark]
    public async Task<object> GetTypeHierarchy()
        => await _engine.GetTypeHierarchyAsync(_routing, _knownTypeId);

    /// <summary>surfaces.list_endpoints. Target: p95 &lt; 50ms</summary>
    [Benchmark]
    public async Task<object> ListEndpoints()
        => await _engine.ListEndpointsAsync(_routing, null, null, 50);

    /// <summary>surfaces.list_config_keys. Target: p95 &lt; 50ms</summary>
    [Benchmark]
    public async Task<object> ListConfigKeys()
        => await _engine.ListConfigKeysAsync(_routing, null, 50);

    /// <summary>surfaces.list_db_tables. Target: p95 &lt; 50ms</summary>
    [Benchmark]
    public async Task<object> ListDbTables()
        => await _engine.ListDbTablesAsync(_routing, null, 50);

    /// <summary>workspace.list — returns all workspaces with IsStale. Target: p95 &lt; 20ms</summary>
    [Benchmark]
    public async Task<object> ListWorkspaces()
        => await _wsMgr.ListWorkspacesAsync(_repoId);

    /// <summary>symbols.get_card via stable ID (sym_ prefix lookup). Target: p95 &lt; 10ms</summary>
    [Benchmark]
    public async Task<object> GetSymbolByStableId()
    {
        if (!_stableIdValid)
            return Task.CompletedTask; // stable IDs not available (old baseline)
        return await _engine.GetSymbolByStableIdAsync(_routing, _knownStableId);
    }
}
