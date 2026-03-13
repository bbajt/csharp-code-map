namespace CodeMap.Integration.Tests.Workflows;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Shared xUnit class fixture that indexes SampleSolution once using real Roslyn.
/// Exposes QueryEngine + BaselineStore for workflow and performance integration tests.
/// Shared across test classes via IClassFixture — indexing only happens once per run.
/// </summary>
public sealed class IndexedSampleSolutionFixture : IAsyncLifetime
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    public static string SampleSolutionDir => Path.GetDirectoryName(SampleSolutionPath)!;

    private string _tempDir = null!;

    // ── Exposed infrastructure ────────────────────────────────────────────────

    public RepoId RepoId { get; } = RepoId.From("workflow-fixture-repo");
    public CommitSha Sha { get; } = CommitSha.From(new string('e', 40));

    public BaselineStore BaselineStore { get; private set; } = null!;
    public QueryEngine QueryEngine { get; private set; } = null!;
    public string OverlayDir { get; private set; } = null!;
    public string BaselineDir { get; private set; } = null!;

    // ── Discovered symbol IDs (runtime, via search) ───────────────────────────

    public SymbolId OrderServiceId { get; private set; }
    public SymbolId IOrderServiceId { get; private set; }
    public SymbolId OrderId { get; private set; }
    public SymbolId AuditableEntityId { get; private set; }
    public SymbolId SubmitAsyncId { get; private set; }
    public SymbolId SaveAsyncId { get; private set; }

    public FilePath OrderServiceFilePath { get; } = FilePath.From("SampleApp/Services/OrderService.cs");

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-workflow-fixture-" + Guid.NewGuid().ToString("N"));
        OverlayDir = Path.Combine(_tempDir, "overlays");
        BaselineDir = Path.Combine(_tempDir, "baselines");
        Directory.CreateDirectory(BaselineDir);
        Directory.CreateDirectory(OverlayDir);

        var factory = new BaselineDbFactory(BaselineDir, NullLogger<BaselineDbFactory>.Instance);
        BaselineStore = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        var compiled = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        await BaselineStore.CreateBaselineAsync(RepoId, Sha, compiled, SampleSolutionDir);

        QueryEngine = new QueryEngine(
            BaselineStore, cache, tracker,
            new ExcerptReader(BaselineStore), new GraphTraverser(),
            new FeatureTracer(BaselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        await DiscoverSymbolsAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public RoutingContext CommittedRouting() =>
        new(repoId: RepoId, baselineCommitSha: Sha);

    private async Task DiscoverSymbolsAsync()
    {
        var routing = CommittedRouting();

        OrderServiceId = await FirstIdAsync(routing, "OrderService", SymbolKind.Class,
            "T:SampleApp.Services.OrderService");

        IOrderServiceId = await FirstIdAsync(routing, "IOrderService", SymbolKind.Interface,
            "T:SampleApp.Services.IOrderService");

        OrderId = await FirstIdEndingWithAsync(routing, "Order", SymbolKind.Class, ".Order",
            "T:SampleApp.Models.Order");

        AuditableEntityId = await FirstIdAsync(routing, "AuditableEntity", SymbolKind.Class,
            "T:SampleApp.Models.AuditableEntity");

        SubmitAsyncId = await FirstIdAsync(routing, "SubmitAsync", SymbolKind.Method,
            "M:SampleApp.Services.OrderService.SubmitAsync(System.String,System.Threading.CancellationToken)");

        SaveAsyncId = await FirstIdAsync(routing, "SaveAsync", SymbolKind.Method,
            "M:SampleApp.Repositories.Repository`1.SaveAsync(`0,System.Threading.CancellationToken)");
    }

    private async Task<SymbolId> FirstIdAsync(
        RoutingContext routing, string query, SymbolKind kind, string fallback)
    {
        var r = await QueryEngine.SearchSymbolsAsync(
            routing, query,
            new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 5));
        if (r.IsSuccess && r.Value.Data.Hits.Count > 0)
            return r.Value.Data.Hits[0].SymbolId;
        return SymbolId.From(fallback);
    }

    private async Task<SymbolId> FirstIdEndingWithAsync(
        RoutingContext routing, string query, SymbolKind kind, string suffix, string fallback)
    {
        var r = await QueryEngine.SearchSymbolsAsync(
            routing, query,
            new SymbolSearchFilters(Kinds: [kind]),
            new BudgetLimits(maxResults: 10));
        if (r.IsSuccess)
        {
            var hit = r.Value.Data.Hits.FirstOrDefault(h => h.SymbolId.Value.EndsWith(suffix));
            if (hit is not null) return hit.SymbolId;
            if (r.Value.Data.Hits.Count > 0) return r.Value.Data.Hits[0].SymbolId;
        }
        return SymbolId.From(fallback);
    }
}
