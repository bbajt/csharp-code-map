namespace CodeMap.Integration.Tests.Summary;

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
/// Integration tests for codemap.summarize / IQueryEngine.SummarizeAsync.
/// Uses manually seeded BaselineStore — no Roslyn compilation.
/// Validates that all 8 FactKind sections appear, filtering works,
/// and project diagnostics are surfaced correctly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CodebaseSummaryIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("summary-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));

    private static readonly FilePath SourceFile = FilePath.From("src/SampleService.cs");

    private static readonly SymbolId MethodSym = SymbolId.From("M:Sample.SampleService.Process");

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly QueryEngine _queryEngine;

    public CodebaseSummaryIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-sum-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var baselineDir = Path.Combine(_tempDir, "baselines");
        Directory.CreateDirectory(baselineDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        _queryEngine = new QueryEngine(
            _baselineStore, cache, tracker,
            new ExcerptReader(_baselineStore), new GraphTraverser(),
            new FeatureTracer(_baselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task SeedBaselineAsync(IReadOnlyList<ExtractedFact> facts, IReadOnlyList<ProjectDiagnostic>? diags = null)
    {
        File.WriteAllText(Path.Combine(_repoDir, "src", "SampleService.cs"), "// stub");

        var card = SymbolCard.CreateMinimal(
            symbolId: MethodSym, fullyQualifiedName: MethodSym.Value,
            kind: SymbolKind.Method, signature: "void Process()",
            @namespace: "Sample", filePath: SourceFile,
            spanStart: 1, spanEnd: 20,
            visibility: "public", confidence: Confidence.High);

        var projectDiags = diags ?? [new ProjectDiagnostic("SampleProject", true, 50, 100)];

        var data = new CompilationResult(
            Symbols: [card],
            References: [],
            Files: [new ExtractedFile("file001", SourceFile, new string('a', 64), null)],
            Stats: new IndexStats(1, 0, 1, 0, Confidence.High, ProjectDiagnostics: projectDiags),
            TypeRelations: [],
            Facts: facts);

        await _baselineStore.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static ExtractedFact MakeFact(FactKind kind, string value, int line = 1) =>
        new(SymbolId: MethodSym,
            StableId: null,
            Kind: kind,
            Value: value,
            FilePath: SourceFile,
            LineStart: line,
            LineEnd: line,
            Confidence: Confidence.High);

    private static RoutingContext MakeRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SummarizeAsync_NoFacts_ReturnsSuccessWithOverviewAndMetrics()
    {
        await SeedBaselineAsync([]);

        var result = await _queryEngine.SummarizeAsync(MakeRouting(), ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.Sections.Should().HaveCount(2); // overview + metrics
        data.Markdown.Should().Contain("# ");
        data.Markdown.Should().Contain("Codebase Summary");
    }

    [Fact]
    public async Task SummarizeAsync_WithAllEightFactKinds_IncludesAllSections()
    {
        await SeedBaselineAsync([
            MakeFact(FactKind.Route,          "GET /api/test",                    1),
            MakeFact(FactKind.Config,          "App:Key|GetValue",                 2),
            MakeFact(FactKind.DbTable,         "Orders|DbSet<Order>",              3),
            MakeFact(FactKind.DiRegistration,  "IFoo → Foo|Scoped",               4),
            MakeFact(FactKind.Middleware,       "UseAuthentication|pos:1",          5),
            MakeFact(FactKind.RetryPolicy,      "RetryAsync(3)|Polly",             6),
            MakeFact(FactKind.Exception,        "ArgumentNullException|throw new", 7),
            MakeFact(FactKind.Log,             "Processing {Id}|Information",      8),
        ]);

        var result = await _queryEngine.SummarizeAsync(MakeRouting(), ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.Sections.Should().HaveCount(10); // overview + 8 fact sections + metrics
        data.Stats.FactCount.Should().Be(8);
        data.Stats.EndpointCount.Should().Be(1);
        data.Stats.ConfigKeyCount.Should().Be(1);
        data.Stats.DbTableCount.Should().Be(1);
        data.Stats.DiRegistrationCount.Should().Be(1);
        data.Stats.ExceptionTypeCount.Should().Be(1);
        data.Stats.LogTemplateCount.Should().Be(1);
    }

    [Fact]
    public async Task SummarizeAsync_SectionFilter_OnlyIncludesRequestedSections()
    {
        await SeedBaselineAsync([
            MakeFact(FactKind.Route,  "GET /api/test", 1),
            MakeFact(FactKind.Config, "App:Key|GetValue", 2),
        ]);

        var result = await _queryEngine.SummarizeAsync(
            MakeRouting(),
            sectionFilter: ["api", "overview"],
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var sections = result.Value!.Data.Sections;
        sections.Should().Contain(s => s.Title.Contains("Solution Overview"));
        sections.Should().Contain(s => s.Title.Contains("API Surface"));
        sections.Should().NotContain(s => s.Title.Contains("Configuration"));
        sections.Should().NotContain(s => s.Title.Contains("Key Metrics"));
    }

    [Fact]
    public async Task SummarizeAsync_WithProjectDiagnostics_PopulatesStats()
    {
        await SeedBaselineAsync([], diags: [
            new ProjectDiagnostic("SampleApp", Compiled: true, SymbolCount: 300, ReferenceCount: 600),
            new ProjectDiagnostic("SampleApp.Api", Compiled: true, SymbolCount: 200, ReferenceCount: 400),
        ]);

        var result = await _queryEngine.SummarizeAsync(MakeRouting(), ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stats = result.Value!.Data.Stats;
        stats.ProjectCount.Should().Be(2);
        stats.SymbolCount.Should().Be(500);
        stats.ReferenceCount.Should().Be(1000);
    }

    [Fact]
    public async Task SummarizeAsync_MaxItemsPerSection_LimitsReturnedFacts()
    {
        await SeedBaselineAsync([
            MakeFact(FactKind.Route, "GET /api/a", 1),
            MakeFact(FactKind.Route, "GET /api/b", 2),
            MakeFact(FactKind.Route, "GET /api/c", 3),
        ]);

        var result = await _queryEngine.SummarizeAsync(
            MakeRouting(),
            maxItemsPerSection: 2,
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var apiSection = result.Value!.Data.Sections.FirstOrDefault(s => s.Title.Contains("API Surface"));
        apiSection.Should().NotBeNull();
        apiSection!.ItemCount.Should().Be(2); // capped at max
    }

    [Fact]
    public async Task SummarizeAsync_InvalidBaseline_ReturnsNotFoundError()
    {
        // Don't seed — baseline doesn't exist
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: CommitSha.From(new string('e', 40)));

        var result = await _queryEngine.SummarizeAsync(routing, ct: CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("INDEX_NOT_AVAILABLE");
    }
}
