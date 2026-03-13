namespace CodeMap.Roslyn.Tests;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class ResolutionWorkerTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From("a".PadRight(40, 'a'));

    private static ResolutionWorker Worker() =>
        new(NullLogger<ResolutionWorker>.Instance);

    private static UnresolvedEdge Edge(
        string toName,
        string? containerHint = null,
        string fromSymbolId = "Foo::Bar",
        string fileId = "file-001",
        int locStart = 10)
        => new(fromSymbolId, toName, containerHint, "Call", fileId, locStart, locStart + 1);

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_EmptyFileList_ReturnsZero()
    {
        var store = Substitute.For<ISymbolStore>();
        var comp = CompilationBuilder.Create("class A {}");

        var count = await Worker().ResolveEdgesForFilesAsync(
            Repo, Sha, [], comp, store);

        count.Should().Be(0);
        await store.DidNotReceive().GetUnresolvedEdgesAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<IReadOnlyList<FilePath>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_NoUnresolvedEdges_ReturnsZero()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Foo.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([]);

        var comp = CompilationBuilder.Create("class A {}");
        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(0);
        await store.DidNotReceive().UpgradeEdgeAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<EdgeUpgrade>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_SingleEdge_UniqueMatch_Upgrades()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([Edge("Execute")]);

        var comp = CompilationBuilder.Create("""
            public class Service
            {
                public void Execute() { }
            }
            """);

        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(1);
        // Doc comment ID format: "M:Service.Execute"
        await store.Received(1).UpgradeEdgeAsync(
            Repo, Sha,
            Arg.Is<EdgeUpgrade>(u =>
                u.FromSymbolId == "Foo::Bar" &&
                u.ResolvedToSymbolId.Value.Contains("Execute", StringComparison.OrdinalIgnoreCase)),
            default);
    }

    [Fact]
    public async Task Resolve_SingleEdge_NoMatch_StaysUnresolved()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([Edge("NonExistentMethod")]);

        var comp = CompilationBuilder.Create("class A { void Run() {} }");
        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(0);
        await store.DidNotReceive().UpgradeEdgeAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<EdgeUpgrade>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_AmbiguousMatch_StaysUnresolved()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([Edge("Process", containerHint: null)]);

        var comp = CompilationBuilder.Create("""
            class A { public void Process() {} }
            class B { public void Process() {} }
            class C { public void Process() {} }
            """);

        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(0);
        await store.DidNotReceive().UpgradeEdgeAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<EdgeUpgrade>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_AmbiguousMatch_ContainerHintDisambiguates()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([Edge("Process", containerHint: "OrderService")]);

        var comp = CompilationBuilder.Create("""
            class UserService   { public void Process() {} }
            class OrderService  { public void Process() {} }
            class ReportService { public void Process() {} }
            """);

        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(1);
        // Doc comment ID format: "M:OrderService.Process" — contains type name
        await store.Received(1).UpgradeEdgeAsync(
            Repo, Sha,
            Arg.Is<EdgeUpgrade>(u => u.ResolvedToSymbolId.Value.Contains("OrderService",
                StringComparison.OrdinalIgnoreCase)),
            default);
    }

    [Fact]
    public async Task Resolve_StableIdPopulated_OnUpgrade()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([Edge("Execute")]);

        var comp = CompilationBuilder.Create("class Worker { public void Execute() {} }");
        await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        await store.Received(1).UpgradeEdgeAsync(
            Repo, Sha,
            Arg.Is<EdgeUpgrade>(u =>
                u.ResolvedStableToId != null &&
                u.ResolvedStableToId.Value.Value.StartsWith("sym_")),
            default);
    }

    [Fact]
    public async Task Resolve_MultipleEdges_ResolvesSome()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };

        var edges = new[]
        {
            Edge("Alpha",    fileId: "f1", locStart: 1),
            Edge("Beta",     fileId: "f1", locStart: 2),
            Edge("Gamma",    fileId: "f1", locStart: 3),
            Edge("Missing1", fileId: "f1", locStart: 4),
            Edge("Missing2", fileId: "f1", locStart: 5),
        };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns(edges);

        var comp = CompilationBuilder.Create("""
            class X
            {
                public void Alpha() {}
                public void Beta()  {}
                public void Gamma() {}
            }
            """);

        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(3);
        await store.Received(3).UpgradeEdgeAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<EdgeUpgrade>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_ContainerHintNull_UniqueNameOnly_Resolves()
    {
        var store = Substitute.For<ISymbolStore>();
        var paths = new[] { FilePath.From("Test.cs") };
        store.GetUnresolvedEdgesAsync(Repo, Sha, Arg.Any<IReadOnlyList<FilePath>>(), default)
             .Returns([Edge("UniqueMethod", containerHint: null)]);

        var comp = CompilationBuilder.Create("class A { public void UniqueMethod() {} }");
        var count = await Worker().ResolveEdgesForFilesAsync(Repo, Sha, paths, comp, store);

        count.Should().Be(1);
    }

    [Fact]
    public async Task ResolveEdgesAsync_AlwaysReturnsZero()
    {
        var count = await Worker().ResolveEdgesAsync(Repo, Sha);
        count.Should().Be(0);
    }
}
