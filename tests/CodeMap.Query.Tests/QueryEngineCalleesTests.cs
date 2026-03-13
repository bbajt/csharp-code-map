namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class QueryEngineCalleesTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("callees-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly SymbolId Caller = SymbolId.From("M:MyNs.Controller.Action");
    private static readonly SymbolId Callee = SymbolId.From("M:MyNs.Service.DoWork");
    private static readonly FilePath File1 = FilePath.From("src/Controller.cs");

    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineCalleesTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, Caller, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Caller));
    }

    [Fact]
    public async Task Callees_ExistingSymbol_ReturnsBfsResult()
    {
        _store.GetOutgoingReferencesAsync(Repo, Sha, Caller, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredOutgoingReference(RefKind.Call, Callee, File1, 5, 5)]);
        _store.GetSymbolAsync(Repo, Sha, Callee, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Callee));

        var result = await _engine.GetCalleesAsync(Routing, Caller, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Root.Should().Be(Caller);
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == Callee);
    }

    [Fact]
    public async Task Callees_SymbolNotFound_ReturnsError()
    {
        var missing = SymbolId.From("M:Missing.Method");
        _store.GetSymbolAsync(Repo, Sha, missing, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.GetCalleesAsync(Routing, missing, depth: 1, limitPerLevel: 20, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Callees_UsesOutgoingRefsDirection()
    {
        // Callees must call GetOutgoingReferencesAsync, NOT GetReferencesAsync
        _store.GetOutgoingReferencesAsync(Repo, Sha, Caller, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        await _engine.GetCalleesAsync(Routing, Caller, depth: 1, limitPerLevel: 20, null);

        await _store.Received(1).GetOutgoingReferencesAsync(
            Repo, Sha, Caller, null, Arg.Any<int>(), Arg.Any<CancellationToken>());
        // GetReferencesAsync (incoming direction) should NOT have been called for root expansion
        await _store.DidNotReceive().GetReferencesAsync(
            Repo, Sha, Caller, null, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callees_FilterToCallAndInstantiate()
    {
        // Read refs should NOT appear as callees
        _store.GetOutgoingReferencesAsync(Repo, Sha, Caller, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([
                  new StoredOutgoingReference(RefKind.Call,  Callee, File1, 5, 5),
                  new StoredOutgoingReference(RefKind.Read,  SymbolId.From("F:MyNs.Class._field"), File1, 6, 6),
                  new StoredOutgoingReference(RefKind.Write, SymbolId.From("F:MyNs.Class._other"), File1, 7, 7),
              ]);
        _store.GetSymbolAsync(Repo, Sha, Callee, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Callee));

        var result = await _engine.GetCalleesAsync(Routing, Caller, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        // Only Call refs should produce callee nodes
        result.Value.Data.Nodes
            .Where(n => n.SymbolId != Caller)
            .Should().AllSatisfy(n => n.SymbolId.Should().Be(Callee));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(SymbolId id) =>
        SymbolCard.CreateMinimal(id, id.Value, SymbolKind.Method, "void Method()",
            "MyNs", File1, 5, 20, "public", Confidence.High);
}
