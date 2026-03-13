namespace CodeMap.Integration.Tests.Cache;

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
/// Integration tests for the shared baseline cache (PHASE-03-08 T02).
/// Tests the full push/pull roundtrip with real temp directories.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BaselineCacheIntegrationTests : IClassFixture<IndexedSampleSolutionFixture>, IDisposable
{
    private readonly IndexedSampleSolutionFixture _f;
    private readonly string _cacheDir;

    public BaselineCacheIntegrationTests(IndexedSampleSolutionFixture fixture)
    {
        _f = fixture;
        _cacheDir = Path.Combine(Path.GetTempPath(), "bcm-int-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best-effort */ }
    }

    private BaselineCacheManager MakeCache(string? cacheDir, string localDir)
    {
        var localFactory = new BaselineDbFactory(localDir, NullLogger<BaselineDbFactory>.Instance);
        return new BaselineCacheManager(localFactory, cacheDir, NullLogger<BaselineCacheManager>.Instance);
    }

    // ── E2E_EnsureBaseline_NoCacheConfigured_BuildsLocally ───────────────────

    [Fact]
    public async Task E2E_EnsureBaseline_NoCacheConfigured_BuildsLocally()
    {
        var localDir = Path.Combine(Path.GetTempPath(), "bcm-int-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localDir);
        try
        {
            var localFactory = new BaselineDbFactory(localDir, NullLogger<BaselineDbFactory>.Instance);
            var localStore = new BaselineStore(localFactory, NullLogger<BaselineStore>.Instance);
            var cacheDisabled = new BaselineCacheManager(localFactory, null, NullLogger<BaselineCacheManager>.Instance);

            var git = Substitute.For<IGitService>();
            var compiler = Substitute.For<IRoslynCompiler>();
            var tempSln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
            File.WriteAllText(tempSln, "");

            git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_f.RepoId);
            git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_f.Sha);
            compiler.CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new CompilationResult([], [], [],
                        new IndexStats(10, 5, 3, 1.0, Confidence.High)));

            var handler = new CodeMap.Mcp.Handlers.IndexHandler(
                git, compiler, localStore, cacheDisabled,
                NullLogger<CodeMap.Mcp.Handlers.IndexHandler>.Instance);

            var result = await handler.HandleAsync(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["repo_path"] = "/fake/repo",
                    ["solution_path"] = tempSln,
                },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            await compiler.Received(1)
                .CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

            // No cache file should exist
            var cacheSubDir = Directory.GetDirectories(_cacheDir, "*", SearchOption.AllDirectories);
            cacheSubDir.Should().BeEmpty("no-op cache should not write any files");

            File.Delete(tempSln);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
        }
    }

    // ── E2E_EnsureBaseline_CacheHit_SkipsCompilation ─────────────────────────

    [Fact]
    public async Task E2E_EnsureBaseline_CacheHit_SkipsCompilation()
    {
        // 1. Push the fixture's real baseline to the shared cache
        var fixtureFactory = new BaselineDbFactory(_f.BaselineDir, NullLogger<BaselineDbFactory>.Instance);
        var pushCache = new BaselineCacheManager(fixtureFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);
        SqliteConnection.ClearAllPools();
        await pushCache.PushAsync(_f.RepoId, _f.Sha);

        var cacheExists = await pushCache.ExistsInCacheAsync(_f.RepoId, _f.Sha);
        cacheExists.Should().BeTrue("baseline must exist in cache before testing pull");

        // 2. Create a fresh local dir (simulates a machine with no local baseline)
        var freshLocalDir = Path.Combine(Path.GetTempPath(), "bcm-int-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshLocalDir);
        try
        {
            var localFactory = new BaselineDbFactory(freshLocalDir, NullLogger<BaselineDbFactory>.Instance);
            var localStore = new BaselineStore(localFactory, NullLogger<BaselineStore>.Instance);
            var cacheManager = new BaselineCacheManager(localFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);

            var git = Substitute.For<IGitService>();
            var compiler = Substitute.For<IRoslynCompiler>();
            var tempSln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
            File.WriteAllText(tempSln, "");

            git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_f.RepoId);
            git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_f.Sha);

            var handler = new CodeMap.Mcp.Handlers.IndexHandler(
                git, compiler, localStore, cacheManager,
                NullLogger<CodeMap.Mcp.Handlers.IndexHandler>.Instance);

            // 3. index.ensure_baseline — should pull from cache, skip compilation
            var result = await handler.HandleAsync(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["repo_path"] = "/fake/repo",
                    ["solution_path"] = tempSln,
                },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            await compiler.DidNotReceive()
                .CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

            // 4. Response should have from_cache = true
            var json = System.Text.Json.JsonDocument.Parse(result.Content).RootElement;
            json.GetProperty("already_existed").GetBoolean().Should().BeTrue();
            json.GetProperty("from_cache").GetBoolean().Should().BeTrue();

            File.Delete(tempSln);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(freshLocalDir)) Directory.Delete(freshLocalDir, true);
        }
    }

    // ── E2E_EnsureBaseline_CacheMiss_BuildsAndPushes ─────────────────────────

    [Fact]
    public async Task E2E_EnsureBaseline_CacheMiss_BuildsAndPushes()
    {
        var localDir = Path.Combine(Path.GetTempPath(), "bcm-int-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localDir);
        var tempSln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
        File.WriteAllText(tempSln, "");

        try
        {
            var localFactory = new BaselineDbFactory(localDir, NullLogger<BaselineDbFactory>.Instance);
            var localStore = new BaselineStore(localFactory, NullLogger<BaselineStore>.Instance);
            var cacheManager = new BaselineCacheManager(localFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);

            var git = Substitute.For<IGitService>();
            var compiler = Substitute.For<IRoslynCompiler>();

            git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_f.RepoId);
            git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_f.Sha);
            compiler.CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new CompilationResult([], [], [],
                        new IndexStats(10, 5, 3, 1.0, Confidence.High)));

            var handler = new CodeMap.Mcp.Handlers.IndexHandler(
                git, compiler, localStore, cacheManager,
                NullLogger<CodeMap.Mcp.Handlers.IndexHandler>.Instance);

            var result = await handler.HandleAsync(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["repo_path"] = "/fake/repo",
                    ["solution_path"] = tempSln,
                },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            await compiler.Received(1)
                .CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

            // Cache should now contain the baseline
            var cacheExists = await cacheManager.ExistsInCacheAsync(_f.RepoId, _f.Sha);
            cacheExists.Should().BeTrue("baseline should be pushed to cache after local build");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
            if (File.Exists(tempSln)) File.Delete(tempSln);
        }
    }

    // ── E2E_EnsureBaseline_CacheCorrupt_FallsBackToBuild ─────────────────────

    [Fact]
    public async Task E2E_EnsureBaseline_CacheCorrupt_FallsBackToBuild()
    {
        // 1. Place a corrupt (0-byte) file in cache at the correct path
        var safeRepo = string.Concat(_f.RepoId.Value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        var corruptDir = Path.Combine(_cacheDir, safeRepo);
        Directory.CreateDirectory(corruptDir);
        var corruptPath = Path.Combine(corruptDir, _f.Sha.Value + ".db");
        File.WriteAllText(corruptPath, ""); // 0-byte = corrupt

        var localDir = Path.Combine(Path.GetTempPath(), "bcm-int-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localDir);
        var tempSln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
        File.WriteAllText(tempSln, "");

        try
        {
            var localFactory = new BaselineDbFactory(localDir, NullLogger<BaselineDbFactory>.Instance);
            var localStore = new BaselineStore(localFactory, NullLogger<BaselineStore>.Instance);
            var cacheManager = new BaselineCacheManager(localFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);

            var git = Substitute.For<IGitService>();
            var compiler = Substitute.For<IRoslynCompiler>();

            git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_f.RepoId);
            git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_f.Sha);
            compiler.CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new CompilationResult([], [], [],
                        new IndexStats(10, 5, 3, 1.0, Confidence.High)));

            var handler = new CodeMap.Mcp.Handlers.IndexHandler(
                git, compiler, localStore, cacheManager,
                NullLogger<CodeMap.Mcp.Handlers.IndexHandler>.Instance);

            // 2. index.ensure_baseline — corrupt cache entry → falls back to Roslyn build
            var result = await handler.HandleAsync(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["repo_path"] = "/fake/repo",
                    ["solution_path"] = tempSln,
                },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            await compiler.Received(1)
                .CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

            // 3. After build, valid baseline pushed to cache (overwrites corrupt)
            var cacheFileSize = new FileInfo(corruptPath).Length;
            cacheFileSize.Should().BeGreaterThan(0, "valid baseline should have overwritten the corrupt cache entry");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
            if (File.Exists(tempSln)) File.Delete(tempSln);
        }
    }

    // ── E2E_PullThenQuery_IndexIsUsable ──────────────────────────────────────

    [Fact]
    public async Task E2E_PullThenQuery_IndexIsUsable()
    {
        // 1. Push the fixture's real baseline to the shared cache
        var fixtureFactory = new BaselineDbFactory(_f.BaselineDir, NullLogger<BaselineDbFactory>.Instance);
        var pushCache = new BaselineCacheManager(fixtureFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);
        SqliteConnection.ClearAllPools();
        await pushCache.PushAsync(_f.RepoId, _f.Sha);

        // 2. Pull into a fresh local dir
        var freshLocalDir = Path.Combine(Path.GetTempPath(), "bcm-int-pull-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshLocalDir);
        try
        {
            var localFactory = new BaselineDbFactory(freshLocalDir, NullLogger<BaselineDbFactory>.Instance);
            var pullCache = new BaselineCacheManager(localFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);

            var pulled = await pullCache.PullAsync(_f.RepoId, _f.Sha);
            pulled.Should().NotBeNull("pull should succeed for a valid cached baseline");

            // 3. Query the pulled baseline
            var localStore = new BaselineStore(localFactory, NullLogger<BaselineStore>.Instance);
            var cacheService = new InMemoryCacheService();
            var tracker = new TokenSavingsTracker();
            var queryEngine = new QueryEngine(
                localStore, cacheService, tracker,
                new ExcerptReader(localStore), new GraphTraverser(),
                new FeatureTracer(localStore, new GraphTraverser()),
                NullLogger<QueryEngine>.Instance);

            var routing = new RoutingContext(repoId: _f.RepoId, baselineCommitSha: _f.Sha);
            var result = await queryEngine.SearchSymbolsAsync(
                routing, "OrderService",
                new SymbolSearchFilters(Kinds: [SymbolKind.Class]),
                new BudgetLimits(maxResults: 5));

            result.IsSuccess.Should().BeTrue();
            result.Value.Data.Hits.Should().NotBeEmpty("pulled baseline should be fully queryable");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(freshLocalDir)) Directory.Delete(freshLocalDir, true);
        }
    }

    // ── E2E_ConcurrentPush_NoCorruption ──────────────────────────────────────

    [Fact]
    public async Task E2E_ConcurrentPush_NoCorruption()
    {
        // 1. Ensure local baseline exists in the fixture
        var fixtureFactory = new BaselineDbFactory(_f.BaselineDir, NullLogger<BaselineDbFactory>.Instance);

        SqliteConnection.ClearAllPools();

        // 2. Push from two tasks simultaneously
        var mgr1 = new BaselineCacheManager(fixtureFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);
        var mgr2 = new BaselineCacheManager(fixtureFactory, _cacheDir, NullLogger<BaselineCacheManager>.Instance);

        await Task.WhenAll(
            mgr1.PushAsync(_f.RepoId, _f.Sha),
            mgr2.PushAsync(_f.RepoId, _f.Sha));

        // 3. Cache file should be a valid SQLite database
        var safeRepo = string.Concat(_f.RepoId.Value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        var cachePath = Path.Combine(_cacheDir, safeRepo, _f.Sha.Value + ".db");
        File.Exists(cachePath).Should().BeTrue();

        SqliteConnection.ClearAllPools();

        using var conn = new SqliteConnection($"Data Source={cachePath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        var count = (long)cmd.ExecuteScalar()!;
        count.Should().BeGreaterThan(0, "cache file should be a valid SQLite DB with schema tables");
    }
}
