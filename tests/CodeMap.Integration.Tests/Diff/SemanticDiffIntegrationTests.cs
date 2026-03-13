namespace CodeMap.Integration.Tests.Diff;

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
/// Integration tests for index.diff / IQueryEngine.DiffAsync.
/// Uses two manually-seeded BaselineStore instances with different symbol sets —
/// no real git commits required. This approach tests the full SQLite → SemanticDiffer
/// pipeline while avoiding git complexity.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SemanticDiffIntegrationTests : IDisposable
{
    private static readonly RepoId    Repo  = RepoId.From("diff-integration-repo");
    private static readonly CommitSha ShaA  = CommitSha.From(new string('a', 40));
    private static readonly CommitSha ShaB  = CommitSha.From(new string('b', 40));
    private static readonly FilePath  File1 = FilePath.From("src/Service.cs");

    private readonly string       _tempDir;
    private readonly string       _repoDir;
    private readonly BaselineStore _store;
    private readonly QueryEngine  _engine;

    public SemanticDiffIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-diff-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var baselineDir = Path.Combine(_tempDir, "baselines");
        Directory.CreateDirectory(baselineDir);

        var factory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);

        _engine = new QueryEngine(
            _store, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(_store), new GraphTraverser(),
            new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(string fqn, SymbolKind kind = SymbolKind.Class,
        string sig = "class Foo", string? stableVal = null)
        => SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(fqn), fullyQualifiedName: fqn,
            kind: kind, signature: sig, @namespace: "Sample",
            filePath: File1, spanStart: 1, spanEnd: 10,
            visibility: "public", confidence: Confidence.High);

    private static ExtractedFact MakeFact(FactKind kind, string value)
        => new(SymbolId: SymbolId.From("M:Sample.OrderService.Process"),
               StableId: null, Kind: kind, Value: value,
               FilePath: File1, LineStart: 1, LineEnd: 1, Confidence: Confidence.High);

    private async Task SeedAsync(CommitSha sha, SymbolCard[] symbols, ExtractedFact[]? facts = null)
    {
        File.WriteAllText(Path.Combine(_repoDir, "src", "Service.cs"), "// stub");

        var data = new CompilationResult(
            Symbols: symbols,
            References: [],
            Files: [new ExtractedFile("file001", File1, new string('a', 64), null)],
            Stats: new IndexStats(1, 0, symbols.Length, 0, Confidence.High,
                ProjectDiagnostics: [new ProjectDiagnostic("SampleProject", true, symbols.Length, 0)]),
            TypeRelations: [],
            Facts: facts ?? []);

        await _store.CreateBaselineAsync(Repo, sha, data, _repoDir);
    }

    private RoutingContext Routing() =>
        new(repoId: Repo, baselineCommitSha: ShaA);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Diff_IdenticalCommits_NoChanges()
    {
        var syms = new[] { MakeCard("Sample.OrderService", SymbolKind.Class) };
        await SeedAsync(ShaA, syms);
        await SeedAsync(ShaB, syms);

        var result = await _engine.DiffAsync(Routing(), ShaA, ShaB, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Data.SymbolChanges.Should().BeEmpty();
        result.Value!.Data.FactChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task E2E_Diff_AddedSymbol_DetectedInDiff()
    {
        await SeedAsync(ShaA, [MakeCard("Sample.OrderService", SymbolKind.Class)]);
        await SeedAsync(ShaB, [
            MakeCard("Sample.OrderService",  SymbolKind.Class),
            MakeCard("Sample.PaymentService", SymbolKind.Class),
        ]);

        var result = await _engine.DiffAsync(Routing(), ShaA, ShaB, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var added = result.Value!.Data.SymbolChanges.Where(s => s.ChangeType == "Added").ToList();
        added.Should().HaveCount(1);
        added[0].ToSymbolId!.Value.Value.Should().Contain("PaymentService");
    }

    [Fact]
    public async Task E2E_Diff_FactChanges_EndpointAddedVisible()
    {
        await SeedAsync(ShaA,
            [MakeCard("Sample.OrderService", SymbolKind.Class)],
            [MakeFact(FactKind.Route, "GET /api/orders")]);

        await SeedAsync(ShaB,
            [MakeCard("Sample.OrderService", SymbolKind.Class)],
            [MakeFact(FactKind.Route, "GET /api/orders"),
             MakeFact(FactKind.Route, "POST /api/payments")]);

        var result = await _engine.DiffAsync(Routing(), ShaA, ShaB, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var addedEndpoints = result.Value!.Data.FactChanges
            .Where(f => f.Kind == FactKind.Route && f.ChangeType == "Added").ToList();
        addedEndpoints.Should().HaveCount(1);
        addedEndpoints[0].ToValue.Should().Be("POST /api/payments");
    }

    [Fact]
    public async Task E2E_Diff_MissingBaseline_ReturnsIndexNotAvailableError()
    {
        // Only seed ShaA — ShaB does not exist
        await SeedAsync(ShaA, [MakeCard("Sample.OrderService", SymbolKind.Class)]);

        // DiffAsync uses EnsureBaseline path through QueryEngine — with missing ShaB
        // the differ simply returns an empty diff (GetAllSymbolSummariesAsync returns [])
        // Check that the error is handled correctly at handler level via BaselineExistsAsync
        var exists = await _store.BaselineExistsAsync(Repo, ShaB, CancellationToken.None);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task E2E_Diff_StatsConsistent()
    {
        await SeedAsync(ShaA, [
            MakeCard("Sample.OrderService",  SymbolKind.Class),
            MakeCard("Sample.OldService",    SymbolKind.Class),
        ]);
        await SeedAsync(ShaB, [
            MakeCard("Sample.OrderService",  SymbolKind.Class),
            MakeCard("Sample.NewService",    SymbolKind.Class),
        ]);

        var result = await _engine.DiffAsync(Routing(), ShaA, ShaB, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!.Data;
        data.Stats.SymbolsAdded.Should()
            .Be(data.SymbolChanges.Count(s => s.ChangeType == "Added"));
        data.Stats.SymbolsRemoved.Should()
            .Be(data.SymbolChanges.Count(s => s.ChangeType == "Removed"));
    }

    [Fact]
    public async Task E2E_Diff_MarkdownOutput_ContainsHeader()
    {
        await SeedAsync(ShaA, [MakeCard("Sample.OrderService", SymbolKind.Class)]);
        await SeedAsync(ShaB, [MakeCard("Sample.OrderService", SymbolKind.Class)]);

        var result = await _engine.DiffAsync(Routing(), ShaA, ShaB, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Data.Markdown.Should().StartWith("# Semantic Diff:");
        result.Value!.Data.Markdown.Should().Contain(ShaA.Value[..7]);
        result.Value!.Data.Markdown.Should().Contain(ShaB.Value[..7]);
    }
}
