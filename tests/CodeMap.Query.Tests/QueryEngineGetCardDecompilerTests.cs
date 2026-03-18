namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class QueryEngineGetCardDecompilerTests
{
    private static readonly RepoId Repo = RepoId.From("decomp-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));
    private static readonly SymbolId SymId = SymbolId.From("T:Foo.Bar");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly IMetadataResolver _resolver = Substitute.For<IMetadataResolver>();
    private readonly InMemoryCacheService _cache = new();

    private QueryEngine CreateEngine() => new QueryEngine(
        _store, _cache, Substitute.For<ITokenSavingsTracker>(),
        new ExcerptReader(_store), new GraphTraverser(),
        new FeatureTracer(_store, new GraphTraverser()),
        NullLogger<QueryEngine>.Instance, _resolver);

    private SymbolCard MakeCard(int isDecompiled) => SymbolCard.CreateMinimal(
        SymId, "Foo.Bar", SymbolKind.Class, "public class Bar", "Foo",
        FilePath.From("decompiled/Foo/Foo/Bar.cs"), 0, 0, "public", Confidence.High)
        with { IsDecompiled = isDecompiled };

    [Fact]
    public async Task GetSymbolCardAsync_MetadataStubWithIncludeCode_TriggersDecompilation()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(1), MakeCard(2));
        _resolver.TryDecompileTypeAsync(SymId, Repo, Sha, Arg.Any<CancellationToken>())
            .Returns("decompiled/Foo/Foo/Bar.cs");

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.IsDecompiled.Should().Be(2);
        await _resolver.Received(1).TryDecompileTypeAsync(SymId, Repo, Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSymbolCardAsync_DecompilationFails_CardHasIsDecompiledOne()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(1));
        _resolver.TryDecompileTypeAsync(SymId, Repo, Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.IsDecompiled.Should().Be(1);
    }

    [Fact]
    public async Task GetSymbolCardAsync_MetadataStubWithIncludeCodeFalse_NoDecompilationAttempt()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(1));

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId, includeCode: false);

        result.IsSuccess.Should().BeTrue();
        await _resolver.DidNotReceive().TryDecompileTypeAsync(
            Arg.Any<SymbolId>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSymbolCardAsync_SourceSymbol_NoDecompilationAttempt()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard(0));

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        await _resolver.DidNotReceive().TryDecompileTypeAsync(
            Arg.Any<SymbolId>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }
}
