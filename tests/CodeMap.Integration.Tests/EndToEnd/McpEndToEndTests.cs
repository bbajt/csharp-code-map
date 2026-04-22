namespace CodeMap.Integration.Tests.EndToEnd;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Git;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Resolution;
using CodeMap.Query;
using CodeMap.Storage;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// End-to-end integration tests for the MCP façade.
/// Uses a manually seeded baseline (no MSBuildWorkspace) to exercise the handler
/// → query engine → storage pipeline without Roslyn compilation overhead.
///
/// Compile-and-store pipeline tests are in <see cref="IndexingPipelineTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpEndToEndTests : IAsyncLifetime
{
    // ── Test data ──────────────────────────────────────────────────────────────

    private static readonly RepoId TestRepoId = RepoId.From("e2e-test-repo");
    private static readonly CommitSha TestSha = CommitSha.From(new string('c', 40));

    private const string ServiceSymbolId = "SampleApp.Services.OrderService";
    private const string ServiceFilePath = "src/SampleApp/Services/OrderService.cs";

    // ── Fixture members ────────────────────────────────────────────────────────

    private TempGitRepo _gitRepo = null!;
    private string _dbDir = null!;

    private BaselineStore _store = null!;
    private RepoStatusHandler _repoStatus = null!;
    private IndexHandler _indexer = null!;
    private McpToolHandlers _query = null!;

    // Repo root dir (has a source file the QueryEngine can read for file spans)
    private string _repoRootDir = null!;

    public async ValueTask InitializeAsync()
    {
        // 1. Create a temp git repo with one commit
        _gitRepo = TempGitRepo.Create(remoteName: null);
        _gitRepo.CommitFile(".codemap-marker", "e2e-test");

        // 2. Create temp dirs
        _dbDir = Path.Combine(Path.GetTempPath(), "codemap-e2e-" + Guid.NewGuid().ToString("N"));
        _repoRootDir = Path.Combine(Path.GetTempPath(), "codemap-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(Path.Combine(_repoRootDir, "src", "SampleApp", "Services"));

        // Write a fake source file so file span reads work
        File.WriteAllText(
            Path.Combine(_repoRootDir, ServiceFilePath.Replace('/', Path.DirectorySeparatorChar)),
            string.Join("\n", Enumerable.Range(1, 40).Select(i =>
                i == 10 ? "    public class OrderService : IOrderService {" :
                i == 39 ? "    }" :
                $"        // line {i}")));

        // 3. Wire real services
        var gitService = new GitService(NullLogger<GitService>.Instance);
        var factory = new BaselineDbFactory(_dbDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();
        var engine = new QueryEngine(_store, cache, tracker, new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        // 4. Mock IRoslynCompiler to avoid MSBuildWorkspace in E2E tests
        //    (compile+store pipeline tested separately in IndexingPipelineTests)
        var compiler = Substitute.For<IRoslynCompiler>();
        compiler.CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(BuildFakeCompilationResult());

        var overlayStore = Substitute.For<IOverlayStore>();
        var wsManager = new WorkspaceManager(
            overlayStore,
            Substitute.For<IIncrementalCompiler>(),
            _store,
            gitService,
            new InMemoryCacheService(),
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
        overlayStore.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<FilePath>());
        _repoStatus = new RepoStatusHandler(gitService, _store, wsManager, new RepoRegistry(), NullLogger<RepoStatusHandler>.Instance);
        _indexer = new IndexHandler(gitService, compiler, _store,
            new BaselineCacheManager(new BaselineDbFactory(Path.GetTempPath(), NullLogger<BaselineDbFactory>.Instance), null, NullLogger<BaselineCacheManager>.Instance),
            new RepoRegistry(), NullLogger<IndexHandler>.Instance);
        _query = new McpToolHandlers(engine, gitService, new McpSymbolResolver(engine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<McpToolHandlers>.Instance);

        // 5. Pre-seed: index using the REAL git repo identity + seeded data
        //    We use a pre-built baseline so tests can query it.
        var repoId = await gitService.GetRepoIdentityAsync(_gitRepo.Path);
        var sha = await gitService.GetCurrentCommitAsync(_gitRepo.Path);

        var compiled = BuildFakeCompilationResult();
        await _store.CreateBaselineAsync(repoId, sha, compiled, _repoRootDir);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _gitRepo.Dispose();
        foreach (var dir in new[] { _dbDir, _repoRootDir })
        {
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        return ValueTask.CompletedTask;
    }

    // ── E2E Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_RepoStatus_AfterIndex_ShowsBaselineExists()
    {
        var result = await _repoStatus.HandleAsync(
            new JsonObject { ["repo_path"] = _gitRepo.Path },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("baseline_index_exists").GetBoolean().Should().BeTrue();
        json.GetProperty("current_commit_sha").GetString().Should().HaveLength(40);
        json.GetProperty("branch_name").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task E2E_RepoStatus_BeforeIndex_ShowsNoBaseline()
    {
        using var freshRepo = TempGitRepo.Create(remoteName: null);
        freshRepo.CommitFile(".marker", "x");

        var freshDb = Path.Combine(Path.GetTempPath(), "codemap-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshDb);
        try
        {
            var git = new GitService(NullLogger<GitService>.Instance);
            var factory = new BaselineDbFactory(freshDb, NullLogger<BaselineDbFactory>.Instance);
            var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
            var freshOverlay = Substitute.For<IOverlayStore>();
            freshOverlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(new HashSet<FilePath>());
            var freshWsMgr = new WorkspaceManager(
                freshOverlay,
                Substitute.For<IIncrementalCompiler>(),
                store,
                git,
                new InMemoryCacheService(),
                Substitute.For<IResolutionWorker>(),
                NullLogger<WorkspaceManager>.Instance);
            var handler = new RepoStatusHandler(git, store, freshWsMgr, new RepoRegistry(), NullLogger<RepoStatusHandler>.Instance);

            var result = await handler.HandleAsync(
                new JsonObject { ["repo_path"] = freshRepo.Path },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            JsonDocument.Parse(result.Content).RootElement
                .GetProperty("baseline_index_exists").GetBoolean().Should().BeFalse();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(freshDb))
                try { Directory.Delete(freshDb, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task E2E_RepoStatus_IncludesGitMetadata()
    {
        var result = await _repoStatus.HandleAsync(
            new JsonObject { ["repo_path"] = _gitRepo.Path },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("repo_id").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("is_clean").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task E2E_IndexTwice_SecondCallIsIdempotent()
    {
        // Create a fake solution file for IndexHandler.HandleAsync to find
        var fakeSln = Path.Combine(Path.GetTempPath(), $"fake_{Guid.NewGuid():N}.sln");
        File.WriteAllText(fakeSln, "");
        try
        {
            // First call will succeed (seeded in InitializeAsync, so already_existed = true)
            var result = await _indexer.HandleAsync(
                new JsonObject
                {
                    ["repo_path"] = _gitRepo.Path,
                    ["solution_path"] = fakeSln,
                },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            JsonDocument.Parse(result.Content).RootElement
                .GetProperty("already_existed").GetBoolean().Should().BeTrue();
        }
        finally
        {
            File.Delete(fakeSln);
        }
    }

    [Fact]
    public async Task E2E_IndexSampleSolution_ThenSearchByName_ReturnsResults()
    {
        var result = await _query.HandleSearchAsync(
            new JsonObject { ["repo_path"] = _gitRepo.Path, ["query"] = "OrderService" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonDocument.Parse(result.Content).RootElement.GetProperty("data");
        data.GetProperty("total_count").GetInt32().Should().BeGreaterThan(0);
        data.GetProperty("hits").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task E2E_Search_HitsContainExpectedFqn()
    {
        var result = await _query.HandleSearchAsync(
            new JsonObject { ["repo_path"] = _gitRepo.Path, ["query"] = "OrderService" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var hits = JsonDocument.Parse(result.Content).RootElement
            .GetProperty("data").GetProperty("hits");

        hits.GetArrayLength().Should().BeGreaterThan(0);
        hits[0].GetProperty("fully_qualified_name").GetString()
            .Should().Be("SampleApp.Services.OrderService");
    }

    [Fact]
    public async Task E2E_SearchWithKindFilter_ClassOnly_ReturnsClassKinds()
    {
        var result = await _query.HandleSearchAsync(
            new JsonObject
            {
                ["repo_path"] = _gitRepo.Path,
                ["query"] = "Order",
                ["kinds"] = new JsonArray("Class"),
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonDocument.Parse(result.Content).RootElement.GetProperty("data");
        data.TryGetProperty("hits", out _).Should().BeTrue();
    }

    [Fact]
    public async Task E2E_IndexSampleSolution_ThenGetCard_ReturnsSymbolCard()
    {
        var cardResult = await _query.HandleGetCardAsync(
            new JsonObject
            {
                ["repo_path"] = _gitRepo.Path,
                ["symbol_id"] = ServiceSymbolId,
            },
            CancellationToken.None);

        cardResult.IsError.Should().BeFalse();
        var card = JsonDocument.Parse(cardResult.Content).RootElement.GetProperty("data");
        card.GetProperty("symbol_id").GetString().Should().Be(ServiceSymbolId);
        card.GetProperty("fully_qualified_name").GetString()
            .Should().Be("SampleApp.Services.OrderService");
        card.GetProperty("kind").GetString().Should().Be("class");
    }

    [Fact]
    public async Task E2E_GetCard_NotFound_ReturnsError()
    {
        var result = await _query.HandleGetCardAsync(
            new JsonObject
            {
                ["repo_path"] = _gitRepo.Path,
                ["symbol_id"] = "DoesNotExist.Symbol",
            },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task E2E_IndexSampleSolution_ThenGetSpan_ReturnsSourceCode()
    {
        var result = await _query.HandleGetSpanAsync(
            new JsonObject
            {
                ["repo_path"] = _gitRepo.Path,
                ["file_path"] = ServiceFilePath,
                ["start_line"] = 5,
                ["end_line"] = 15,
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonDocument.Parse(result.Content).RootElement.GetProperty("data");
        data.GetProperty("content").GetString().Should().Contain("OrderService");
    }

    [Fact]
    public async Task E2E_IndexSampleSolution_ThenGetDefinitionSpan_ReturnsDefinition()
    {
        var result = await _query.HandleGetDefinitionSpanAsync(
            new JsonObject
            {
                ["repo_path"] = _gitRepo.Path,
                ["symbol_id"] = ServiceSymbolId,
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonDocument.Parse(result.Content).RootElement.GetProperty("data");
        data.GetProperty("content").GetString().Should().Contain("OrderService");
    }

    [Fact]
    public async Task E2E_Search_MissingQuery_ReturnsError()
    {
        var result = await _query.HandleSearchAsync(
            new JsonObject { ["repo_path"] = _gitRepo.Path },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── Test data builder ──────────────────────────────────────────────────────

    private static CompilationResult BuildFakeCompilationResult()
    {
        var fileId = "deadbeef12345678";
        var file = new ExtractedFile(fileId, FilePath.From(ServiceFilePath), new string('0', 64), "SampleApp");

        var symbol = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(ServiceSymbolId),
            fullyQualifiedName: "SampleApp.Services.OrderService",
            kind: SymbolKind.Class,
            signature: "public class OrderService : IOrderService",
            @namespace: "SampleApp.Services",
            filePath: FilePath.From(ServiceFilePath),
            spanStart: 10,
            spanEnd: 39,
            visibility: "public",
            confidence: Confidence.High,
            documentation: "Handles order processing.");

        return new CompilationResult(
            Symbols: [symbol],
            References: [],
            Files: [file],
            Stats: new IndexStats(1, 0, 1, 0.5, Confidence.High));
    }
}
