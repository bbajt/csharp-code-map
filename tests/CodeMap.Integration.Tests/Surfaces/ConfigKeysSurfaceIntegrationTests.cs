namespace CodeMap.Integration.Tests.Surfaces;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Integration tests for surfaces.list_config_keys.
/// Uses manually seeded BaselineStore + OverlayStore — no Roslyn compilation.
/// Validates filtering, workspace overlay merge, and response structure.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConfigKeysSurfaceIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("surfaces-config-int-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-config-int-01");

    // Files
    private static readonly FilePath ServiceFile = FilePath.From("src/OrderService.cs");
    private static readonly FilePath NewServiceFile = FilePath.From("src/PaymentService.cs");

    // Symbols
    private static readonly SymbolId GetDbSym = SymbolId.From("M:MyApp.OrderService.GetDb");
    private static readonly SymbolId GetRetriesSym = SymbolId.From("M:MyApp.OrderService.GetRetries");
    private static readonly SymbolId GetLevelSym = SymbolId.From("M:MyApp.OrderService.GetLevel");
    private static readonly SymbolId NewPaymentSym = SymbolId.From("M:MyApp.PaymentService.GetTimeout");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public ConfigKeysSurfaceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-cfg-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        _queryEngine = new QueryEngine(
            _baselineStore, cache, tracker,
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            new FeatureTracer(_baselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var gitSvc = Substitute.For<IGitService>();
        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, gitSvc, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceMgr, cache, tracker,
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private async Task SeedBaselineAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "src", "OrderService.cs"), "// stub");

        var data = new CompilationResult(
            Symbols: [
                MakeCard(GetDbSym,      ServiceFile),
                MakeCard(GetRetriesSym, ServiceFile),
                MakeCard(GetLevelSym,   ServiceFile),
            ],
            References: [],
            Files: [new ExtractedFile("file001", ServiceFile, new string('a', 64), null)],
            Stats: new IndexStats(3, 0, 1, 0, Confidence.High),
            TypeRelations: [],
            Facts: [
                MakeFact(GetDbSym,      "ConnectionStrings:DefaultDB|IConfiguration indexer", ServiceFile, 5),
                MakeFact(GetRetriesSym, "App:MaxRetries|GetValue",                            ServiceFile, 10),
                MakeFact(GetLevelSym,   "Logging:LogLevel:Default|GetSection",                ServiceFile, 15),
            ]);

        await _baselineStore.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static SymbolCard MakeCard(SymbolId id, FilePath file) =>
        SymbolCard.CreateMinimal(
            symbolId: id, fullyQualifiedName: id.Value,
            kind: SymbolKind.Method, signature: id.Value + "()",
            @namespace: "MyApp", filePath: file,
            spanStart: 1, spanEnd: 30,
            visibility: "public", confidence: Confidence.High);

    private static ExtractedFact MakeFact(SymbolId symbolId, string value, FilePath file, int line) =>
        new(SymbolId: symbolId,
            StableId: null,
            Kind: FactKind.Config,
            Value: value,
            FilePath: file,
            LineStart: line,
            LineEnd: line + 4,
            Confidence: Confidence.High);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_ListConfigKeys_ReturnsBaselineKeys()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListConfigKeysAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Keys.Should().HaveCount(3);
        result.Value.Data.Keys.Should().Contain(k => k.Key == "ConnectionStrings:DefaultDB");
    }

    [Fact]
    public async Task E2E_ListConfigKeys_KeyFilter_FiltersCorrectly()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListConfigKeysAsync(routing, "App:", 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Keys.Should().OnlyContain(k =>
            k.Key.StartsWith("App:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task E2E_ListConfigKeys_UsagePatternPopulated()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListConfigKeysAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        var patterns = result.Value.Data.Keys.Select(k => k.UsagePattern).ToHashSet();
        patterns.Should().Contain("IConfiguration indexer");
        patterns.Should().Contain("GetValue");
        patterns.Should().Contain("GetSection");
    }

    [Fact]
    public async Task E2E_ListConfigKeys_ResponseStructure()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListConfigKeysAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        foreach (var key in result.Value.Data.Keys)
        {
            key.Key.Should().NotBeNullOrEmpty();
            key.UsedBySymbol.Value.Should().NotBeNullOrEmpty();
            key.FilePath.Value.Should().NotBeNullOrEmpty();
            key.Line.Should().BeGreaterThan(0);
            key.UsagePattern.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task E2E_ListConfigKeys_WorkspaceMode_IncludesOverlayKeys()
    {
        await SeedBaselineAsync();

        File.WriteAllText(Path.Combine(_repoDir, "src", "PaymentService.cs"), "// stub");

        var newCard = MakeCard(NewPaymentSym, NewServiceFile);
        var overlayDelta = new OverlayDelta(
            ReindexedFiles: [new ExtractedFile("file002", NewServiceFile, new string('b', 64), null)],
            AddedOrUpdatedSymbols: [newCard],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            Facts: [MakeFact(NewPaymentSym, "Payment:Timeout|GetValue", NewServiceFile, 5)]);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(overlayDelta));

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);
        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [NewServiceFile]);

        var routing = new RoutingContext(
            repoId: Repo, workspaceId: WsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _mergedEngine.ListConfigKeysAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        var keys = result.Value.Data.Keys;
        keys.Should().Contain(k => k.Key == "Payment:Timeout",
            because: "overlay config key should be included");
        keys.Should().Contain(k => k.Key == "ConnectionStrings:DefaultDB",
            because: "baseline config keys should still be present");
    }
}
