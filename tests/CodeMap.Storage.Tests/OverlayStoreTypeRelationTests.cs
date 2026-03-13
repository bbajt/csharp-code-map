namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// T01 storage tests for type relations in the overlay store.
/// Verifies that ApplyDeltaAsync persists type_relations rows correctly and
/// that a second delta for the same file replaces old relations (clean-slate).
///
/// GetOverlayTypeRelationsAsync / GetOverlayDerivedTypesAsync are added in T02;
/// until then this suite reads back data via a direct SqliteConnection opened
/// against the DB file that OverlayDbFactory knows how to locate.
/// </summary>
public sealed class OverlayStoreTypeRelationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("overlay-type-rel-repo");
    private static readonly WorkspaceId Workspace = WorkspaceId.From("ws-type-rel");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));

    // Symbol IDs used in test data
    private const string AnimalId = "T:MyApp.Animal";
    private const string DogId = "T:MyApp.Dog";
    private const string IRunnableId = "T:MyApp.IRunnable";
    private const string CatId = "T:MyApp.Cat";

    private readonly string _tempDir;
    private readonly OverlayDbFactory _factory;
    private readonly OverlayStore _store;

    public OverlayStoreTypeRelationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
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

    // ── Direct SQL helpers ──────────────────────────────────────────────────

    private long CountTypeRelationRows()
    {
        using var conn = _factory.OpenExisting(Repo, Workspace);
        conn.Should().NotBeNull("overlay DB must exist after CreateOverlayAsync/ApplyDeltaAsync");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM type_relations";
        return (long)cmd.ExecuteScalar()!;
    }

    private List<(string TypeId, string RelatedId, string Kind)> ReadAllTypeRelations()
    {
        using var conn = _factory.OpenExisting(Repo, Workspace);
        conn.Should().NotBeNull("overlay DB must exist after CreateOverlayAsync/ApplyDeltaAsync");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText =
            "SELECT type_symbol_id, related_symbol_id, relation_kind FROM type_relations";

        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private List<(string RelatedId, string Kind)> QueryRelationsFor(string typeId)
    {
        using var conn = _factory.OpenExisting(Repo, Workspace);
        conn.Should().NotBeNull("overlay DB must exist after CreateOverlayAsync/ApplyDeltaAsync");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText =
            "SELECT related_symbol_id, relation_kind " +
            "FROM type_relations WHERE type_symbol_id = $id";
        cmd.Parameters.AddWithValue("$id", typeId);

        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // ── Delta builder helpers ───────────────────────────────────────────────

    private static OverlayDelta MakeDeltaWithRelations(
        string filePath,
        string fileId,
        IReadOnlyList<ExtractedTypeRelation> relations,
        int revision = 1)
    {
        var file = OverlayTestHelpers.MakeFile(filePath, fileId);
        var symbol = OverlayTestHelpers.MakeSymbol(
            DogId, "MyApp.Dog", filePath, SymbolKind.Class);

        return new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [symbol],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: revision,
            TypeRelations: relations);
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDelta_TypeRelationsInserted()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };
        var delta = MakeDeltaWithRelations("src/Dog.cs", "aabbccdd11223344", relations);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        CountTypeRelationRows().Should().Be(2);
    }

    [Fact]
    public async Task ApplyDelta_ReplacesRelationsForReindexedFiles()
    {
        // First delta: Dog extends Animal + implements IRunnable
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var firstRelations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };
        var firstDelta = MakeDeltaWithRelations(
            "src/Dog.cs", "aabbccdd11223344", firstRelations, revision: 1);
        await _store.ApplyDeltaAsync(Repo, Workspace, firstDelta);

        CountTypeRelationRows().Should().Be(2, "first delta inserted 2 relations");

        // Second delta for the same file: Dog now only implements IRunnable (base type removed)
        var secondRelations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };

        // Re-use same file path + fileId so DeleteTypeRelationsForFiles fires
        var file = OverlayTestHelpers.MakeFile("src/Dog.cs", "aabbccdd11223344");
        var updatedSymbol = OverlayTestHelpers.MakeSymbol(DogId, "MyApp.Dog", "src/Dog.cs", SymbolKind.Class);

        var secondDelta = new OverlayDelta(
            ReindexedFiles: [file],
            AddedOrUpdatedSymbols: [updatedSymbol],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 2,
            TypeRelations: secondRelations);

        await _store.ApplyDeltaAsync(Repo, Workspace, secondDelta);

        // Old relations for Dog should have been replaced — only 1 relation now
        CountTypeRelationRows().Should().Be(1,
            "second delta should replace (not accumulate) relations for the re-indexed file");

        var dogRelations = QueryRelationsFor(DogId);
        dogRelations.Should().ContainSingle();
        dogRelations[0].RelatedId.Should().Be(IRunnableId);
        dogRelations[0].Kind.Should().Be("Interface");
    }

    [Fact]
    public async Task GetOverlayTypeRelations_ReturnsCorrectly()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };
        var delta = MakeDeltaWithRelations("src/Dog.cs", "aabbccdd11223344", relations);
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        var rows = ReadAllTypeRelations();
        rows.Should().HaveCount(2);
        rows.Should().Contain(r => r.TypeId == DogId && r.RelatedId == AnimalId && r.Kind == "BaseType");
        rows.Should().Contain(r => r.TypeId == DogId && r.RelatedId == IRunnableId && r.Kind == "Interface");
    }

    [Fact]
    public async Task ApplyDelta_NullTypeRelations_TypeRelationsTableEmpty()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // Delta with no TypeRelations — table should remain empty
        var delta = OverlayTestHelpers.MakeDelta();  // TypeRelations defaults to null
        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        CountTypeRelationRows().Should().Be(0);
    }

    [Fact]
    public async Task ApplyDelta_TwoFilesWithRelations_BothStored()
    {
        await _store.CreateOverlayAsync(Repo, Workspace, Sha);

        // Two separate files, each producing one relation
        var dogFile = OverlayTestHelpers.MakeFile("src/Dog.cs", "aabbccdd11223344");
        var catFile = OverlayTestHelpers.MakeFile("src/Cat.cs", "11223344aabbccdd");
        var dogSymbol = OverlayTestHelpers.MakeSymbol(DogId, "MyApp.Dog", "src/Dog.cs", SymbolKind.Class);
        var catSymbol = OverlayTestHelpers.MakeSymbol(CatId, "MyApp.Cat", "src/Cat.cs", SymbolKind.Class);

        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(CatId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
        };

        var delta = new OverlayDelta(
            ReindexedFiles: [dogFile, catFile],
            AddedOrUpdatedSymbols: [dogSymbol, catSymbol],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: 1,
            TypeRelations: relations);

        await _store.ApplyDeltaAsync(Repo, Workspace, delta);

        CountTypeRelationRows().Should().Be(2);
        var rows = ReadAllTypeRelations();
        rows.Should().Contain(r => r.TypeId == DogId && r.RelatedId == AnimalId && r.Kind == "BaseType");
        rows.Should().Contain(r => r.TypeId == CatId && r.RelatedId == AnimalId && r.Kind == "BaseType");
    }
}
