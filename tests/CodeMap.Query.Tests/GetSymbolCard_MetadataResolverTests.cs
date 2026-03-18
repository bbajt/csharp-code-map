namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class GetSymbolCard_MetadataResolverTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly IMetadataResolver _resolver = Substitute.For<IMetadataResolver>();
    private readonly InMemoryCacheService _cache = new();

    private static readonly RepoId Repo = RepoId.From("meta-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly SymbolId SymId = SymbolId.From("M:System.String.Format(System.String)");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    private QueryEngine CreateEngine() => new QueryEngine(
        _store, _cache, _tracker,
        new ExcerptReader(_store),
        new GraphTraverser(),
        new FeatureTracer(_store, new GraphTraverser()),
        NullLogger<QueryEngine>.Instance,
        _resolver);

    private static SymbolCard MakeCard() => SymbolCard.CreateMinimal(
        symbolId: SymId,
        fullyQualifiedName: "M:System.String.Format(System.String)",
        kind: SymbolKind.Method,
        signature: "string Format(string)",
        @namespace: "System",
        filePath: FilePath.From("decompiled/System.Runtime/System/String.cs"),
        spanStart: 0,
        spanEnd: 0,
        visibility: "public",
        confidence: Confidence.High);

    [Fact]
    public async Task GetCard_SymbolNotInDb_MetadataResolverCalled()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns((SymbolCard?)null);
        _resolver.TryResolveTypeAsync(SymId, Repo, Sha, Arg.Any<CancellationToken>()).Returns(0);

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
        await _resolver.Received(1).TryResolveTypeAsync(SymId, Repo, Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_MetadataResolverInsertsStubs_ReturnsCard()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        // First call returns null (miss), second call returns the stub card
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns((SymbolCard?)null, MakeCard());
        _resolver.TryResolveTypeAsync(SymId, Repo, Sha, Arg.Any<CancellationToken>()).Returns(5);

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.SymbolId.Should().Be(SymId);
    }

    [Fact]
    public async Task GetCard_NoMetadataResolver_ReturnsNotFound()
    {
        // Engine constructed without resolver
        var engine = new QueryEngine(
            _store, _cache, _tracker,
            new ExcerptReader(_store),
            new GraphTraverser(),
            new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns((SymbolCard?)null);

        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
        await _resolver.DidNotReceive().TryResolveTypeAsync(Arg.Any<SymbolId>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_SymbolFoundDirectly_ResolverNotCalled()
    {
        _store.BaselineExistsAsync(Repo, Sha).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId).Returns(MakeCard());

        var engine = CreateEngine();
        var result = await engine.GetSymbolCardAsync(Routing, SymId);

        result.IsSuccess.Should().BeTrue();
        await _resolver.DidNotReceive().TryResolveTypeAsync(Arg.Any<SymbolId>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }
}
