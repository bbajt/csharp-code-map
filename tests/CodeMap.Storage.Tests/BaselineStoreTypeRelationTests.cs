namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

/// <summary>
/// T01 storage tests for type relations in the baseline store.
/// Verifies that CreateBaselineAsync persists type_relations rows correctly.
///
/// GetTypeRelationsAsync / GetDerivedTypesAsync are added in T02; until then
/// this suite reads back data via a direct SqliteConnection opened against the
/// DB file that BaselineDbFactory knows how to locate.
/// </summary>
public class BaselineStoreTypeRelationTests : IDisposable
{
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = StorageTestHelpers.TestSha;

    // Symbols used in test data
    private const string AnimalId = "T:MyApp.Animal";
    private const string DogId = "T:MyApp.Dog";
    private const string IRunnableId = "T:MyApp.IRunnable";
    private const string RunnerId = "T:MyApp.Runner";
    private const string ISerializableId = "T:MyApp.ISerializable";

    private readonly string _tempDir;
    private readonly BaselineStore _store;
    private readonly BaselineDbFactory _factory;

    public BaselineStoreTypeRelationTests()
    {
        (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();

        // Keep a factory reference so tests can open the DB directly for SQL queries
        _factory = new BaselineDbFactory(_tempDir,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BaselineDbFactory>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CompilationResult MakeResultWithRelations(
        IReadOnlyList<ExtractedTypeRelation> relations)
    {
        // Provide at least one file so the compilation result is not completely empty
        var file = StorageTestHelpers.MakeFile("src/Types.cs", "aabbccdd11223344");
        return new CompilationResult(
            Symbols: [],
            References: [],
            Files: [file],
            Stats: new IndexStats(0, 0, 1, 0, Confidence.High),
            TypeRelations: relations);
    }

    /// <summary>
    /// Opens the stored baseline DB directly and counts rows in type_relations.
    /// </summary>
    private long CountTypeRelationRows()
    {
        using var conn = _factory.OpenExisting(Repo, Sha);
        conn.Should().NotBeNull("baseline DB must exist after CreateBaselineAsync");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM type_relations";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Reads all rows from type_relations into anonymous tuples for assertions.
    /// </summary>
    private List<(string TypeId, string RelatedId, string Kind, string Display)>
        ReadAllTypeRelations()
    {
        using var conn = _factory.OpenExisting(Repo, Sha);
        conn.Should().NotBeNull("baseline DB must exist after CreateBaselineAsync");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText =
            "SELECT type_symbol_id, related_symbol_id, relation_kind, display_name " +
            "FROM type_relations";

        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string, string)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return rows;
    }

    /// <summary>
    /// Queries type_relations for all rows where type_symbol_id = <paramref name="typeId"/>.
    /// </summary>
    private List<(string RelatedId, string Kind)> QueryRelationsFor(string typeId)
    {
        using var conn = _factory.OpenExisting(Repo, Sha);
        conn.Should().NotBeNull("baseline DB must exist after CreateBaselineAsync");

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

    /// <summary>
    /// Queries type_relations for all rows where related_symbol_id = <paramref name="relatedId"/>.
    /// This is the "derived types" direction.
    /// </summary>
    private List<(string TypeId, string Kind)> QueryDerivedFor(string relatedId)
    {
        using var conn = _factory.OpenExisting(Repo, Sha);
        conn.Should().NotBeNull("baseline DB must exist after CreateBaselineAsync");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText =
            "SELECT type_symbol_id, relation_kind " +
            "FROM type_relations WHERE related_symbol_id = $id";
        cmd.Parameters.AddWithValue("$id", relatedId);

        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBaseline_TypeRelationsInserted()
    {
        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(RunnerId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };

        await _store.CreateBaselineAsync(Repo, Sha, MakeResultWithRelations(relations));

        CountTypeRelationRows().Should().Be(2);
    }

    [Fact]
    public async Task GetTypeRelations_ReturnsBaseTypeAndInterfaces()
    {
        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };

        await _store.CreateBaselineAsync(Repo, Sha, MakeResultWithRelations(relations));

        var dogRelations = QueryRelationsFor(DogId);

        dogRelations.Should().HaveCount(2);
        dogRelations.Should().Contain((AnimalId, "BaseType"));
        dogRelations.Should().Contain((IRunnableId, "Interface"));
    }

    [Fact]
    public async Task GetTypeRelations_NotFound_ReturnsEmpty()
    {
        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
        };

        await _store.CreateBaselineAsync(Repo, Sha, MakeResultWithRelations(relations));

        // Query for a type that has no stored relations
        var rows = QueryRelationsFor("T:MyApp.UnknownType");
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDerivedTypes_ReturnsTypesWithMatchingBaseOrInterface()
    {
        // Both Dog and Runner relate to IRunnable (one base, one interface)
        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
            new ExtractedTypeRelation(SymbolId.From(RunnerId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };

        await _store.CreateBaselineAsync(Repo, Sha, MakeResultWithRelations(relations));

        // Reverse lookup: who has IRunnable as a related symbol?
        var derived = QueryDerivedFor(IRunnableId);
        derived.Should().HaveCount(2);
        derived.Should().Contain((DogId, "Interface"));
        derived.Should().Contain((RunnerId, "Interface"));

        // Reverse lookup: who derives from Animal?
        var animalDerived = QueryDerivedFor(AnimalId);
        animalDerived.Should().ContainSingle();
        animalDerived[0].TypeId.Should().Be(DogId);
        animalDerived[0].Kind.Should().Be("BaseType");
    }

    [Fact]
    public async Task CreateBaseline_NoTypeRelations_TypeRelationsTableEmpty()
    {
        // CompilationResult with null TypeRelations — table should remain empty
        var file = StorageTestHelpers.MakeFile("src/Foo.cs", "aabbccdd11223344");
        var data = new CompilationResult([], [], [file],
            new IndexStats(0, 0, 1, 0, Confidence.High),
            TypeRelations: null);

        await _store.CreateBaselineAsync(Repo, Sha, data);

        CountTypeRelationRows().Should().Be(0);
    }

    [Fact]
    public async Task CreateBaseline_MultipleTypesWithInterfaces_AllRowsStored()
    {
        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(ISerializableId),
                TypeRelationKind.Interface, "ISerializable"),
            new ExtractedTypeRelation(SymbolId.From(RunnerId), SymbolId.From(IRunnableId),
                TypeRelationKind.Interface, "IRunnable"),
        };

        await _store.CreateBaselineAsync(Repo, Sha, MakeResultWithRelations(relations));

        CountTypeRelationRows().Should().Be(4);

        var allRows = ReadAllTypeRelations();
        allRows.Should().Contain(r => r.TypeId == DogId && r.RelatedId == AnimalId && r.Kind == "BaseType");
        allRows.Should().Contain(r => r.TypeId == DogId && r.RelatedId == IRunnableId && r.Kind == "Interface");
        allRows.Should().Contain(r => r.TypeId == DogId && r.RelatedId == ISerializableId && r.Kind == "Interface");
        allRows.Should().Contain(r => r.TypeId == RunnerId && r.RelatedId == IRunnableId && r.Kind == "Interface");
    }

    [Fact]
    public async Task CreateBaseline_TypeRelation_DisplayNameStored()
    {
        var relations = new[]
        {
            new ExtractedTypeRelation(SymbolId.From(DogId), SymbolId.From(AnimalId),
                TypeRelationKind.BaseType, "Animal"),
        };

        await _store.CreateBaselineAsync(Repo, Sha, MakeResultWithRelations(relations));

        var rows = ReadAllTypeRelations();
        rows.Should().ContainSingle();
        rows[0].Display.Should().Be("Animal");
    }
}
