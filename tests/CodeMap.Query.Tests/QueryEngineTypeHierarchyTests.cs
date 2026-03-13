namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class QueryEngineTypeHierarchyTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("hierarchy-repo");
    private static readonly CommitSha Sha = CommitSha.From("0000000000000000000000000000000000000001");
    private static readonly SymbolId Target = SymbolId.From("T:MyNs.MyClass");
    private static readonly SymbolId BaseId = SymbolId.From("T:MyNs.BaseClass");
    private static readonly SymbolId IfaceId = SymbolId.From("T:MyNs.IMyInterface");
    private static readonly SymbolId DerivedId = SymbolId.From("T:MyNs.DerivedClass");
    private static readonly FilePath File1 = FilePath.From("src/MyClass.cs");

    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineTypeHierarchyTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
    }

    // ── Test cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hierarchy_ClassWithBase_ReturnsBaseType()
    {
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target, SymbolKind.Class));
        _store.GetTypeRelationsAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[
                  new StoredTypeRelation(Target, BaseId, TypeRelationKind.BaseType, "BaseClass")
              ]);
        _store.GetDerivedTypesAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);

        var result = await _engine.GetTypeHierarchyAsync(Routing, Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.BaseType.Should().NotBeNull();
        result.Value.Data.BaseType!.SymbolId.Should().Be(BaseId);
        result.Value.Data.BaseType.DisplayName.Should().Be("BaseClass");
    }

    [Fact]
    public async Task Hierarchy_ClassWithInterfaces_ReturnsInterfaces()
    {
        var iface2 = SymbolId.From("T:MyNs.ISecond");
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target, SymbolKind.Class));
        _store.GetTypeRelationsAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[
                  new StoredTypeRelation(Target, IfaceId, TypeRelationKind.Interface, "IMyInterface"),
                  new StoredTypeRelation(Target, iface2,  TypeRelationKind.Interface, "ISecond"),
              ]);
        _store.GetDerivedTypesAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);

        var result = await _engine.GetTypeHierarchyAsync(Routing, Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Interfaces.Should().HaveCount(2);
        result.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == IfaceId);
        result.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == iface2);
    }

    [Fact]
    public async Task Hierarchy_ClassWithDerived_ReturnsDerivedTypes()
    {
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target, SymbolKind.Class));
        _store.GetTypeRelationsAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);
        _store.GetDerivedTypesAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[
                  new StoredTypeRelation(DerivedId, Target, TypeRelationKind.BaseType, "DerivedClass")
              ]);

        var result = await _engine.GetTypeHierarchyAsync(Routing, Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.DerivedTypes.Should().HaveCount(1);
        result.Value.Data.DerivedTypes[0].SymbolId.Should().Be(DerivedId);
        result.Value.Data.DerivedTypes[0].DisplayName.Should().Be("DerivedClass");
    }

    [Fact]
    public async Task Hierarchy_InterfaceNoBase_BaseTypeNull()
    {
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target, SymbolKind.Interface));
        _store.GetTypeRelationsAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);
        _store.GetDerivedTypesAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);

        var result = await _engine.GetTypeHierarchyAsync(Routing, Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.BaseType.Should().BeNull();
    }

    [Fact]
    public async Task Hierarchy_SymbolNotFound_ReturnsError()
    {
        var missing = SymbolId.From("T:MyNs.Missing");
        _store.GetSymbolAsync(Repo, Sha, missing, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.GetTypeHierarchyAsync(Routing, missing);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Hierarchy_NonTypeSymbol_ReturnsInvalidArgument()
    {
        var methodId = SymbolId.From("M:MyNs.MyClass.DoWork");
        _store.GetSymbolAsync(Repo, Sha, methodId, Arg.Any<CancellationToken>())
              .Returns(MakeCard(methodId, SymbolKind.Method));

        var result = await _engine.GetTypeHierarchyAsync(Routing, methodId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Hierarchy_CacheHit_ReturnsCachedWithoutStoreRelationCalls()
    {
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target, SymbolKind.Class));
        _store.GetTypeRelationsAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);
        _store.GetDerivedTypesAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);

        // First call populates cache
        await _engine.GetTypeHierarchyAsync(Routing, Target);
        // Second call should hit cache
        var result = await _engine.GetTypeHierarchyAsync(Routing, Target);

        result.IsSuccess.Should().BeTrue();
        // Relations should only have been fetched once (cache hit on second call)
        await _store.Received(1).GetTypeRelationsAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
        await _store.Received(1).GetDerivedTypesAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hierarchy_NoRelations_ReturnsEmptyListsAndNullBase()
    {
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target, SymbolKind.Class));
        _store.GetTypeRelationsAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);
        _store.GetDerivedTypesAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<StoredTypeRelation>)[]);

        var result = await _engine.GetTypeHierarchyAsync(Routing, Target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.TargetType.Should().Be(Target);
        result.Value.Data.BaseType.Should().BeNull();
        result.Value.Data.Interfaces.Should().BeEmpty();
        result.Value.Data.DerivedTypes.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(SymbolId id, SymbolKind kind) =>
        SymbolCard.CreateMinimal(id, id.Value, kind, $"{kind} {id.Value}",
            "MyNs", File1, 1, 10, "public", Confidence.High);
}
