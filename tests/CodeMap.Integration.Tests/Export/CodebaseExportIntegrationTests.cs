namespace CodeMap.Integration.Tests.Export;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Integration tests for codemap.export / IQueryEngine.ExportAsync.
/// Uses manually seeded BaselineStore — no Roslyn compilation.
/// Validates that detail levels, formats, token budget, and section filtering work.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CodebaseExportIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("export-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('f', 40));

    private static readonly FilePath SourceFile = FilePath.From("src/SampleService.cs");
    private static readonly SymbolId MethodSym = SymbolId.From("M:Sample.SampleService.Process");

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly QueryEngine _queryEngine;

    public CodebaseExportIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-exp-int-" + Guid.NewGuid().ToString("N"));
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

    private async Task SeedBaselineAsync(IReadOnlyList<ExtractedFact>? facts = null)
    {
        File.WriteAllText(Path.Combine(_repoDir, "src", "SampleService.cs"), "// stub");

        var card = SymbolCard.CreateMinimal(
            symbolId: MethodSym, fullyQualifiedName: MethodSym.Value,
            kind: SymbolKind.Method, signature: "void Process()",
            @namespace: "Sample", filePath: SourceFile,
            spanStart: 1, spanEnd: 20,
            visibility: "public", confidence: Confidence.High);

        var data = new CompilationResult(
            Symbols: [card],
            References: [],
            Files: [new ExtractedFile("file001", SourceFile, new string('a', 64), null)],
            Stats: new IndexStats(1, 0, 1, 0, Confidence.High,
                ProjectDiagnostics: [new ProjectDiagnostic("SampleProject", true, 1, 0)]),
            TypeRelations: [],
            Facts: facts ?? []);

        await _baselineStore.CreateBaselineAsync(Repo, Sha, data, _repoDir);
    }

    private static RoutingContext MakeRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportAsync_SummaryDetail_ReturnsMarkdownWithHeader()
    {
        await SeedBaselineAsync();

        var result = await _queryEngine.ExportAsync(MakeRouting(), detail: "summary", format: "markdown",
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.Format.Should().Be("markdown");
        data.DetailLevel.Should().Be("summary");
        data.Content.Should().StartWith("#");
        data.Content.Should().Contain("Codebase Context");
    }

    [Fact]
    public async Task ExportAsync_JsonFormat_ReturnsValidJsonWithSolutionKey()
    {
        await SeedBaselineAsync();

        var result = await _queryEngine.ExportAsync(MakeRouting(), detail: "summary", format: "json",
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.Format.Should().Be("json");
        data.Content.Should().StartWith("{");
        data.Content.Should().Contain("\"solution\"");
        data.Content.Should().Contain("\"sections\"");
        data.Content.Should().Contain("\"stats\"");
    }

    [Fact]
    public async Task ExportAsync_EstimatedTokens_MatchesContentLength()
    {
        await SeedBaselineAsync();

        var result = await _queryEngine.ExportAsync(MakeRouting(), detail: "summary", format: "markdown",
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.EstimatedTokens.Should().Be(data.Content.Length / 4);
    }

    [Fact]
    public async Task ExportAsync_StandardDetail_ReturnsSuccessWithSummarySections()
    {
        await SeedBaselineAsync([
            new ExtractedFact(SymbolId: MethodSym, StableId: null, Kind: FactKind.Route,
                Value: "GET /api/test", FilePath: SourceFile, LineStart: 1, LineEnd: 1,
                Confidence: Confidence.High),
        ]);

        var result = await _queryEngine.ExportAsync(MakeRouting(), detail: "standard", format: "markdown",
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.DetailLevel.Should().Be("standard");
        data.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExportAsync_InvalidBaseline_ReturnsIndexNotAvailableError()
    {
        // Do NOT seed — baseline does not exist
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: Sha);

        var result = await _queryEngine.ExportAsync(routing, ct: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INDEX_NOT_AVAILABLE");
    }
}
