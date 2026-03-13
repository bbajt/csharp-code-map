namespace CodeMap.Integration.Tests.Performance;

using System.Diagnostics;
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
/// Lightweight timing checks for M03 surface tools (PHASE-03-09 T02).
/// Verifies queries complete within 2× the informal p95 targets.
///
/// Targets (2× p95):
///   surfaces.list_endpoints  → &lt; 120 ms  (p95: 60 ms)
///   surfaces.list_config_keys → &lt; 120 ms
///   surfaces.list_db_tables   → &lt; 120 ms
///   symbols.get_card + facts  → &lt; 40 ms  (p95: 20 ms)
///   workspace.list            → &lt; 60 ms  (p95: 30 ms)
///   cache pull                → &lt; 500 ms (hard target from spec)
///
/// Each test discards the first (cold) run and checks the median of the remaining runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class M03PerformanceTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public M03PerformanceTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long MedianMs(IReadOnlyList<long> times)
    {
        var sorted = times.OrderBy(t => t).ToList();
        return sorted[sorted.Count / 2];
    }

    private static async Task<List<long>> RunTimedAsync(Func<Task> action, int runs = 4)
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
    public async Task Perf_ListEndpoints_Under120ms()
    {
        await _f.QueryEngine.ListEndpointsAsync(Routing, null, null, 50); // warm-up

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.ListEndpointsAsync(Routing, null, null, 50));

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(120,
            $"surfaces.list_endpoints median={median}ms must be ≤ 120ms (2× p95=60ms)");
    }

    [Fact]
    public async Task Perf_ListConfigKeys_Under120ms()
    {
        await _f.QueryEngine.ListConfigKeysAsync(Routing, null, 50); // warm-up

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.ListConfigKeysAsync(Routing, null, 50));

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(120,
            $"surfaces.list_config_keys median={median}ms must be ≤ 120ms (2× p95=60ms)");
    }

    [Fact]
    public async Task Perf_ListDbTables_Under120ms()
    {
        await _f.QueryEngine.ListDbTablesAsync(Routing, null, 50); // warm-up

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.ListDbTablesAsync(Routing, null, 50));

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(120,
            $"surfaces.list_db_tables median={median}ms must be ≤ 120ms (2× p95=60ms)");
    }

    [Fact]
    public async Task Perf_GetCardWithFacts_Under40ms()
    {
        await _f.QueryEngine.GetSymbolCardAsync(Routing, _f.OrderServiceId); // warm-up

        var times = await RunTimedAsync(async () =>
            await _f.QueryEngine.GetSymbolCardAsync(Routing, _f.OrderServiceId));

        var median = MedianMs(times);
        median.Should().BeLessOrEqualTo(40,
            $"symbols.get_card+facts median={median}ms must be ≤ 40ms (2× p95=20ms)");
    }

    [Fact]
    public async Task Perf_WorkspaceList_Under60ms()
    {
        var overlayDir = Path.Combine(
            Path.GetTempPath(), "codemap-m03-perf-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overlayDir);

        try
        {
            var factory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
            var store = new OverlayStore(factory, NullLogger<OverlayStore>.Instance);

            var git = Substitute.For<IGitService>();
            git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(_f.Sha));

            var wsMgr = new WorkspaceManager(
                store,
                Substitute.For<IIncrementalCompiler>(),
                _f.BaselineStore,
                git,
                new InMemoryCacheService(),
                Substitute.For<IResolutionWorker>(),
                NullLogger<WorkspaceManager>.Instance);

            await wsMgr.ListWorkspacesAsync(_f.RepoId); // warm-up

            var times = await RunTimedAsync(async () =>
                await wsMgr.ListWorkspacesAsync(_f.RepoId));

            var median = MedianMs(times);
            median.Should().BeLessOrEqualTo(60,
                $"workspace.list median={median}ms must be ≤ 60ms (2× p95=30ms)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(overlayDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Perf_CachePull_Under500ms()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), "codemap-m03-perf-cache-" + Guid.NewGuid().ToString("N"));
        var localDir = Path.Combine(tempDir, "local");
        var cacheDir = Path.Combine(tempDir, "cache");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(cacheDir);

        try
        {
            // Push the fixture's baseline to the temp cache
            var srcFactory = new BaselineDbFactory(_f.BaselineDir, NullLogger<BaselineDbFactory>.Instance);
            var pushMgr = new BaselineCacheManager(
                srcFactory, cacheDir, NullLogger<BaselineCacheManager>.Instance);
            await pushMgr.PushAsync(_f.RepoId, _f.Sha);

            // Measure the pull from cache to a new local dir
            SqliteConnection.ClearAllPools();
            var pullFactory = new BaselineDbFactory(localDir, NullLogger<BaselineDbFactory>.Instance);
            var pullMgr = new BaselineCacheManager(
                pullFactory, cacheDir, NullLogger<BaselineCacheManager>.Instance);

            var sw = Stopwatch.StartNew();
            var pulled = await pullMgr.PullAsync(_f.RepoId, _f.Sha);
            sw.Stop();

            pulled.Should().NotBeNull("cache pull must succeed");
            sw.ElapsedMilliseconds.Should().BeLessOrEqualTo(500,
                $"cache pull elapsed={sw.ElapsedMilliseconds}ms must be < 500ms");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
