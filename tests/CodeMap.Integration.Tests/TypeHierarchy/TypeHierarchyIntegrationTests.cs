namespace CodeMap.Integration.Tests.TypeHierarchy;

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
/// End-to-end integration tests for types.hierarchy (PHASE-02-06 T02/T04).
/// Uses manually seeded BaselineStore + OverlayStore — no Roslyn compilation.
///
/// Seeded type hierarchy:
///   AuditableEntity : IEntity
///   Order : AuditableEntity (also transitively : IEntity)
///   SoftDeletableEntity : AuditableEntity
/// </summary>
[Trait("Category", "Integration")]
public sealed class TypeHierarchyIntegrationTests : IDisposable
{
    // ── Type symbol IDs for the seeded hierarchy ───────────────────────────────

    private static readonly RepoId Repo = RepoId.From("hierarchy-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-hierarchy-int-01");

    // Types
    private static readonly SymbolId IEntity = SymbolId.From("T:MyNs.IEntity");
    private static readonly SymbolId AuditableEntity = SymbolId.From("T:MyNs.AuditableEntity");
    private static readonly SymbolId Order = SymbolId.From("T:MyNs.Order");
    private static readonly SymbolId SoftDeletable = SymbolId.From("T:MyNs.SoftDeletableEntity");
    private static readonly SymbolId Standalone = SymbolId.From("T:MyNs.Standalone");

    // Files
    private static readonly FilePath IEntityFile = FilePath.From("src/IEntity.cs");
    private static readonly FilePath AuditableEntityFile = FilePath.From("src/AuditableEntity.cs");
    private static readonly FilePath OrderFile = FilePath.From("src/Order.cs");
    private static readonly FilePath SoftDeletableFile = FilePath.From("src/SoftDeletable.cs");
    private static readonly FilePath StandaloneFile = FilePath.From("src/Standalone.cs");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public TypeHierarchyIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-hier-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        // Write minimal source files
        WriteSourceFile("IEntity.cs", "namespace MyNs { public interface IEntity { int Id { get; } } }");
        WriteSourceFile("AuditableEntity.cs", "namespace MyNs { public abstract class AuditableEntity : IEntity { public int Id { get; set; } } }");
        WriteSourceFile("Order.cs", "namespace MyNs { public class Order : AuditableEntity { } }");
        WriteSourceFile("SoftDeletable.cs", "namespace MyNs { public class SoftDeletableEntity : AuditableEntity { public bool IsDeleted { get; set; } } }");
        WriteSourceFile("Standalone.cs", "namespace MyNs { public class Standalone { } }");

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        SeedBaseline();

        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var cache = new InMemoryCacheService();

        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _queryEngine = new QueryEngine(
            _baselineStore, cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), new FeatureTracer(_baselineStore, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceMgr,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(OverlayDelta.Empty(1)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Hierarchy_ClassWithBase_ReturnsBaseType()
    {
        var result = await _queryEngine.GetTypeHierarchyAsync(CommittedRouting(), Order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.TargetType.Should().Be(Order);
        result.Value.Data.BaseType.Should().NotBeNull();
        result.Value.Data.BaseType!.SymbolId.Should().Be(AuditableEntity);
    }

    [Fact]
    public async Task E2E_Hierarchy_ClassWithInterface_ReturnsInterfaces()
    {
        var result = await _queryEngine.GetTypeHierarchyAsync(CommittedRouting(), AuditableEntity);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == IEntity);
    }

    [Fact]
    public async Task E2E_Hierarchy_Interface_ReturnsDerivedImplementors()
    {
        // AuditableEntity implements IEntity → IEntity.DerivedTypes should contain AuditableEntity
        var result = await _queryEngine.GetTypeHierarchyAsync(CommittedRouting(), IEntity);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.DerivedTypes.Should().Contain(r => r.SymbolId == AuditableEntity);
    }

    [Fact]
    public async Task E2E_Hierarchy_NoRelations_ReturnsEmptyLists()
    {
        var result = await _queryEngine.GetTypeHierarchyAsync(CommittedRouting(), Standalone);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.BaseType.Should().BeNull();
        result.Value.Data.Interfaces.Should().BeEmpty();
        result.Value.Data.DerivedTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task E2E_Hierarchy_WorkspaceMode_OverlayTypeRelationsUsed()
    {
        // Create a new interface that Order will implement in the overlay
        var iNewIface = SymbolId.From("T:MyNs.INewInterface");
        var iNewIfaceFile = FilePath.From("src/INewInterface.cs");
        WriteSourceFile("INewInterface.cs", "namespace MyNs { public interface INewInterface { } }");

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);

        var overlayRelation = new ExtractedTypeRelation(Order, iNewIface, TypeRelationKind.Interface, "INewInterface");

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("order001new", OrderFile, new string('f', 64), null)],
                     AddedOrUpdatedSymbols: [MakeCard(Order, OrderFile)],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     TypeRelations: [overlayRelation],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [OrderFile]);

        var wsRouting = WorkspaceRouting();
        var result = await _mergedEngine.GetTypeHierarchyAsync(wsRouting, Order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Interfaces.Should().Contain(r => r.SymbolId == iNewIface);
    }

    [Fact]
    public async Task E2E_Hierarchy_CommittedMode_IgnoresOverlay()
    {
        // Even after workspace creation, committed mode returns baseline data only
        var iOverlayIface = SymbolId.From("T:MyNs.IOverlayOnly");
        WriteSourceFile("IOverlayOnly.cs", "namespace MyNs { public interface IOverlayOnly { } }");

        await _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir);

        var overlayRelation = new ExtractedTypeRelation(Order, iOverlayIface, TypeRelationKind.Interface, "IOverlayOnly");

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new OverlayDelta(
                     ReindexedFiles: [new ExtractedFile("order001ovl", OrderFile, new string('a', 64), null)],
                     AddedOrUpdatedSymbols: [MakeCard(Order, OrderFile)],
                     DeletedSymbolIds: [],
                     AddedOrUpdatedReferences: [],
                     DeletedReferenceFiles: [],
                     TypeRelations: [overlayRelation],
                     NewRevision: 1)));

        await _workspaceMgr.RefreshOverlayAsync(Repo, WsId, [OrderFile]);

        // Committed mode — overlay interface should NOT appear
        var result = await _queryEngine.GetTypeHierarchyAsync(CommittedRouting(), Order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Interfaces.Should().NotContain(r => r.SymbolId == iOverlayIface);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private static RoutingContext WorkspaceRouting() =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

    private void WriteSourceFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_repoDir, "src", name), content);

    private void SeedBaseline()
    {
        // Symbols
        var symIEntity = MakeCard(IEntity, IEntityFile, kind: SymbolKind.Interface);
        var symAuditableEntity = MakeCard(AuditableEntity, AuditableEntityFile, kind: SymbolKind.Class);
        var symOrder = MakeCard(Order, OrderFile, kind: SymbolKind.Class);
        var symSoftDeletable = MakeCard(SoftDeletable, SoftDeletableFile, kind: SymbolKind.Class);
        var symStandalone = MakeCard(Standalone, StandaloneFile, kind: SymbolKind.Class);

        // Files
        var fIEntity = new ExtractedFile("ient001", IEntityFile, new string('1', 64), null);
        var fAuditableEntity = new ExtractedFile("audit001", AuditableEntityFile, new string('2', 64), null);
        var fOrder = new ExtractedFile("ord001", OrderFile, new string('3', 64), null);
        var fSoftDeletable = new ExtractedFile("soft001", SoftDeletableFile, new string('4', 64), null);
        var fStandalone = new ExtractedFile("stan001", StandaloneFile, new string('5', 64), null);

        // Type relations:
        // AuditableEntity : IEntity
        // Order : AuditableEntity
        // SoftDeletableEntity : AuditableEntity
        var typeRelations = new List<ExtractedTypeRelation>
        {
            new(AuditableEntity, IEntity,         TypeRelationKind.Interface, "IEntity"),
            new(Order,           AuditableEntity, TypeRelationKind.BaseType,  "AuditableEntity"),
            new(SoftDeletable,   AuditableEntity, TypeRelationKind.BaseType,  "AuditableEntity"),
        };

        var compilation = new CompilationResult(
            Symbols: [symIEntity, symAuditableEntity, symOrder, symSoftDeletable, symStandalone],
            References: [],
            Files: [fIEntity, fAuditableEntity, fOrder, fSoftDeletable, fStandalone],
            TypeRelations: typeRelations,
            Stats: new IndexStats(5, 0, 5, 0.1, Confidence.High));

        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, _repoDir)
                      .GetAwaiter().GetResult();
    }

    private static SymbolCard MakeCard(SymbolId id, FilePath file, SymbolKind kind = SymbolKind.Class) =>
        SymbolCard.CreateMinimal(id, id.Value, kind, $"{kind} {id.Value.Split('.').Last()}",
            "MyNs", file, 1, 10, "public", Confidence.High);
}
