namespace CodeMap.Integration.Tests.Performance;

using System.Diagnostics;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Integration.Tests.Workflows;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Lightweight timing checks for M02 tools (PHASE-02-07 T03).
/// Verifies queries complete within 2× the p95 targets from SYSTEM-ARCHITECTURE.MD.
/// The 2× multiplier provides CI/test-environment headroom without masking regressions.
///
/// Targets (2× p95):
///   refs.find (limit=50)       → &lt; 160 ms   (p95: 80 ms)
///   graph.callers (depth=1)    → &lt; 100 ms
///   graph.callers (depth=2)    → &lt; 300 ms   (p95: 150 ms)
///   graph.callees (depth=1)    → &lt; 100 ms
///   types.hierarchy            → &lt; 60 ms    (p95: 30 ms)
///   incremental reindex        → &lt; 400 ms   (p95: 200 ms)
///   symbols.search             → &lt; 60 ms    (p95: 30 ms)
///
/// Each test discards the first (cold) run and checks the median of the remaining runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class M02PerformanceTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public M02PerformanceTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long MedianMs(IReadOnlyList<long> times)
    {
        var sorted = times.OrderBy(t => t).ToList();
        return sorted[sorted.Count / 2];
    }

    private static async Task<List<long>> RunTimedAsync(Func<Task> action, int runs = 5)
    {
        var times = new List<long>(runs);
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }
        return times;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Perf_RefsFind_Under160ms()
    {
        // warm-up (cold cache)
        await _f.QueryEngine.FindReferencesAsync(
            Routing, _f.SubmitAsyncId, null, new BudgetLimits(maxResults: 50));

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.FindReferencesAsync(
                Routing, _f.SubmitAsyncId, null, new BudgetLimits(maxResults: 50)), runs: 4);

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(160,
            $"refs.find median={median}ms must be ≤ 160ms (2× p95=80ms)");
    }

    [Fact]
    public async Task Perf_GraphCallers_Depth1_Under100ms()
    {
        await _f.QueryEngine.GetCallersAsync(
            Routing, _f.SubmitAsyncId, depth: 1, limitPerLevel: 20, budgets: null);

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.GetCallersAsync(
                Routing, _f.SubmitAsyncId, depth: 1, limitPerLevel: 20, budgets: null), runs: 4);

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(100,
            $"graph.callers(depth=1) median={median}ms must be ≤ 100ms");
    }

    [Fact]
    public async Task Perf_GraphCallers_Depth2_Under300ms()
    {
        await _f.QueryEngine.GetCallersAsync(
            Routing, _f.SaveAsyncId, depth: 2, limitPerLevel: 20, budgets: null);

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.GetCallersAsync(
                Routing, _f.SaveAsyncId, depth: 2, limitPerLevel: 20, budgets: null), runs: 4);

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(300,
            $"graph.callers(depth=2) median={median}ms must be ≤ 300ms (2× p95=150ms)");
    }

    [Fact]
    public async Task Perf_GraphCallees_Depth1_Under100ms()
    {
        await _f.QueryEngine.GetCalleesAsync(
            Routing, _f.SubmitAsyncId, depth: 1, limitPerLevel: 20, budgets: null);

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.GetCalleesAsync(
                Routing, _f.SubmitAsyncId, depth: 1, limitPerLevel: 20, budgets: null), runs: 4);

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(100,
            $"graph.callees(depth=1) median={median}ms must be ≤ 100ms");
    }

    [Fact]
    public async Task Perf_TypesHierarchy_Under60ms()
    {
        await _f.QueryEngine.GetTypeHierarchyAsync(Routing, _f.OrderId);

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.GetTypeHierarchyAsync(Routing, _f.OrderId), runs: 4);

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(60,
            $"types.hierarchy median={median}ms must be ≤ 60ms (2× p95=30ms)");
    }

    [Fact]
    public async Task Perf_IncrementalReindex_Under400ms()
    {
        var overlayDir = Path.Combine(
            Path.GetTempPath(), "codemap-perf-ovl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overlayDir);

        try
        {
            var factory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
            var overlay = new OverlayStore(factory, NullLogger<OverlayStore>.Instance);
            var compiler = Substitute.For<IIncrementalCompiler>();
            compiler.ComputeDeltaAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                        Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                        Arg.Any<int>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(OverlayDelta.Empty(1)));

            var git = Substitute.For<IGitService>();
            git.GetChangedFilesAsync(
                    Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

            var wsMgr = new WorkspaceManager(
                overlay, compiler, _f.BaselineStore, git, new InMemoryCacheService(),
                Substitute.For<IResolutionWorker>(),
                NullLogger<WorkspaceManager>.Instance);

            var wsId = WorkspaceId.From("ws-perf-reindex");
            await wsMgr.CreateWorkspaceAsync(
                _f.RepoId, wsId, _f.Sha, "/fake/solution.sln",
                IndexedSampleSolutionFixture.SampleSolutionDir);

            // warm-up
            await wsMgr.RefreshOverlayAsync(_f.RepoId, wsId, [_f.OrderServiceFilePath]);

            var sw = Stopwatch.StartNew();
            await wsMgr.RefreshOverlayAsync(_f.RepoId, wsId, [_f.OrderServiceFilePath]);
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeLessOrEqualTo(400,
                $"incremental reindex elapsed={sw.ElapsedMilliseconds}ms must be ≤ 400ms (2× p95=200ms)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(overlayDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Perf_SymbolSearch_Under60ms()
    {
        await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "OrderService",
            new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
            new BudgetLimits(maxResults: 20));

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.SearchSymbolsAsync(
                Routing, "OrderService",
                new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
                new BudgetLimits(maxResults: 20)), runs: 4);

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(60,
            $"symbols.search median={median}ms must be ≤ 60ms (2× p95=30ms)");
    }
}
