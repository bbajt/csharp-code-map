namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class OverlayStoreFactsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    private static readonly RepoId Repo = RepoId.From("overlay-facts-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-facts");
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));

    private static readonly FilePath File1 = FilePath.From("src/Api.cs");
    private static readonly SymbolId Sym1 = SymbolId.From("M:App.Controller.Get");
    private static readonly StableId Stable = new("sym_" + new string('f', 16));

    public OverlayStoreFactsTests()
    {
        Directory.CreateDirectory(_tempDir);
        _factory = new OverlayDbFactory(_tempDir, NullLogger<OverlayDbFactory>.Instance);
        _store = new OverlayStore(_factory, NullLogger<OverlayStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task CreateOverlayAsync()
        => await _store.CreateOverlayAsync(Repo, Workspace, Sha);

    private static SymbolCard MakeCard() =>
        SymbolCard.CreateMinimal(
            symbolId: Sym1,
            fullyQualifiedName: "Controller.Get",
            kind: SymbolKind.Method,
            signature: "IActionResult Get()",
            @namespace: "App",
            filePath: File1,
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High);

    private static ExtractedFact MakeFact(
        string value = "GET /api/orders",
        FactKind kind = FactKind.Route,
        StableId? stableId = null) =>
        new(
            SymbolId: Sym1,
            StableId: stableId ?? Stable,
            Kind: kind,
            Value: value,
            FilePath: File1,
            LineStart: 5,
            LineEnd: 8,
            Confidence: Confidence.High);

    private async Task ApplyDeltaWithFactsAsync(IReadOnlyList<ExtractedFact> facts)
    {
        var file = new ExtractedFile("file001", File1, new string('a', 64), null);
        var delta = new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [MakeCard()],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            Facts: facts);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDelta_WithFacts_InsertedCorrectly()
    {
        await CreateOverlayAsync();
        await ApplyDeltaWithFactsAsync([MakeFact()]);

        var facts = await _store.GetOverlayFactsByKindAsync(Repo, Workspace, FactKind.Route, 10);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("GET /api/orders");
        facts[0].SymbolId.Should().Be(Sym1);
        facts[0].FilePath.Should().Be(File1);
        facts[0].StableId.Should().NotBeNull();
        facts[0].StableId!.Value.Should().Be(Stable);
    }

    [Fact]
    public async Task GetOverlayFacts_ByKind_ReturnsCorrectly()
    {
        await CreateOverlayAsync();
        await ApplyDeltaWithFactsAsync([
            MakeFact("GET /api/orders", FactKind.Route),
            MakeFact("ConnectionStrings:Db", FactKind.Config),
        ]);

        var routes = await _store.GetOverlayFactsByKindAsync(Repo, Workspace, FactKind.Route, 10);
        var configs = await _store.GetOverlayFactsByKindAsync(Repo, Workspace, FactKind.Config, 10);

        routes.Should().HaveCount(1).And.OnlyContain(f => f.Kind == FactKind.Route);
        configs.Should().HaveCount(1).And.OnlyContain(f => f.Kind == FactKind.Config);
    }

    [Fact]
    public async Task GetOverlayFacts_EmptyOverlay_ReturnsEmpty()
    {
        await CreateOverlayAsync();

        var facts = await _store.GetOverlayFactsByKindAsync(Repo, Workspace, FactKind.Route, 10);

        facts.Should().BeEmpty();
    }
}
