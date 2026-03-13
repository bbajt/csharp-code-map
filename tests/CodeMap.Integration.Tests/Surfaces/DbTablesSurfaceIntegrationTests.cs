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
/// Integration tests for surfaces.list_db_tables.
/// Uses manually seeded BaselineStore + OverlayStore — no Roslyn compilation.
/// Validates filtering, aggregation (ReferencedBy), workspace overlay merge, and response structure.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DbTablesSurfaceIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("surfaces-db-int-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-db-int-01");

    // Files
    private static readonly FilePath DataFile = FilePath.From("src/AppDbContext.cs");
    private static readonly FilePath NewFile = FilePath.From("src/ShopDbContext.cs");

    // Symbols
    private static readonly SymbolId OrdersProp = SymbolId.From("P:MyApp.Data.AppDbContext.Orders");
    private static readonly SymbolId CustomersProp = SymbolId.From("P:MyApp.Data.AppDbContext.Customers");
    private static readonly SymbolId SqlMethodSym = SymbolId.From("M:MyApp.Data.AppDbContext.RunReport");
    private static readonly SymbolId NewTableProp = SymbolId.From("P:MyApp.Data.ShopDbContext.Products");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public DbTablesSurfaceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-db-int-" + Guid.NewGuid().ToString("N"));
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
        File.WriteAllText(Path.Combine(_repoDir, "src", "AppDbContext.cs"), "// stub");

        var data = new CompilationResult(
            Symbols: [
                MakeCard(OrdersProp,    DataFile),
                MakeCard(CustomersProp, DataFile),
                MakeCard(SqlMethodSym,  DataFile),
            ],
            References: [],
            Files: [new ExtractedFile("file001", DataFile, new string('a', 64), null)],
            Stats: new IndexStats(3, 0, 1, 0, Confidence.High),
            TypeRelations: [],
            Facts: [
                MakeFact(OrdersProp,    "Orders|DbSet<Order>",          DataFile, 5,  Confidence.High),
                MakeFact(CustomersProp, "Customers|DbSet<Customer>",    DataFile, 6,  Confidence.High),
                MakeFact(SqlMethodSym,  "Orders|Raw SQL",               DataFile, 20, Confidence.Medium),
            ]);

        await _baselineStore.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static SymbolCard MakeCard(SymbolId id, FilePath file) =>
        SymbolCard.CreateMinimal(
            symbolId: id, fullyQualifiedName: id.Value,
            kind: SymbolKind.Method, signature: id.Value + "()",
            @namespace: "MyApp.Data", filePath: file,
            spanStart: 1, spanEnd: 30,
            visibility: "public", confidence: Confidence.High);

    private static ExtractedFact MakeFact(
        SymbolId symbolId, string value, FilePath file, int line, Confidence confidence) =>
        new(SymbolId: symbolId,
            StableId: null,
            Kind: FactKind.DbTable,
            Value: value,
            FilePath: file,
            LineStart: line,
            LineEnd: line + 1,
            Confidence: confidence);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_ListDbTables_ReturnsBaselineTables()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListDbTablesAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        // Orders (from DbSet + Raw SQL), Customers (from DbSet) = 2 grouped tables
        result.Value.Data.Tables.Should().HaveCount(2);
        result.Value.Data.Tables.Should().Contain(t => t.TableName == "Orders");
        result.Value.Data.Tables.Should().Contain(t => t.TableName == "Customers");
    }

    [Fact]
    public async Task E2E_ListDbTables_TableFilter_FiltersCorrectly()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListDbTablesAsync(routing, "Order", 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Tables.Should().OnlyContain(t =>
            t.TableName.StartsWith("Order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task E2E_ListDbTables_ReferencedByAggregated()
    {
        // Orders table is referenced by both OrdersProp (DbSet) and SqlMethodSym (Raw SQL)
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListDbTablesAsync(routing, "Order", 50);

        result.IsSuccess.Should().BeTrue();
        var ordersTable = result.Value.Data.Tables.Single(t => t.TableName == "Orders");
        ordersTable.ReferencedBy.Should().HaveCount(2,
            because: "both the DbSet property and Raw SQL method reference Orders");
    }

    [Fact]
    public async Task E2E_ListDbTables_ResponseStructure()
    {
        await SeedBaselineAsync();
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ListDbTablesAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        foreach (var table in result.Value.Data.Tables)
        {
            table.TableName.Should().NotBeNullOrEmpty();
            table.ReferencedBy.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task E2E_ListDbTables_WorkspaceMode_IncludesOverlayTables()
    {
        await SeedBaselineAsync();

        File.WriteAllText(Path.Combine(_repoDir, "src", "ShopDbContext.cs"), "// stub");

        var newCard = MakeCard(NewTableProp, NewFile);
        var overlayDelta = new OverlayDelta(
            ReindexedFiles: [new ExtractedFile("file002", NewFile, new string('f', 64), null)],
            AddedOrUpdatedSymbols: [newCard],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            Facts: [MakeFact(NewTableProp, "Products|DbSet<Product>", NewFile, 5, Confidence.High)]);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(overlayDelta));

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);
        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [NewFile]);

        var routing = new RoutingContext(
            repoId: Repo, workspaceId: WsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await _mergedEngine.ListDbTablesAsync(routing, null, 50);

        result.IsSuccess.Should().BeTrue();
        var tables = result.Value.Data.Tables;
        tables.Should().Contain(t => t.TableName == "Products",
            because: "overlay DB table should be included");
        tables.Should().Contain(t => t.TableName == "Orders",
            because: "baseline DB tables should still be present");
    }
}
