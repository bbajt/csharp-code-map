namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class QueryEngineCallersTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("callers-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));
    private static readonly SymbolId Target = SymbolId.From("M:MyNs.Service.DoWork");
    private static readonly FilePath File1 = FilePath.From("src/Service.cs");

    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    public QueryEngineCallersTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, Target, Arg.Any<CancellationToken>())
              .Returns(MakeCard(Target));
    }

    [Fact]
    public async Task Callers_ExistingSymbol_ReturnsBfsResult()
    {
        var caller = SymbolId.From("M:MyNs.Controller.Action");
        _store.GetReferencesAsync(Repo, Sha, Target, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, caller, File1, 5, 5, null)]);
        _store.GetSymbolAsync(Repo, Sha, caller, Arg.Any<CancellationToken>())
              .Returns(MakeCard(caller));

        var result = await _engine.GetCallersAsync(Routing, Target, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Root.Should().Be(Target);
        result.Value.Data.Nodes.Should().Contain(n => n.SymbolId == caller);
    }

    [Fact]
    public async Task Callers_SymbolNotFound_ReturnsError()
    {
        var missing = SymbolId.From("M:Missing.Method");
        _store.GetSymbolAsync(Repo, Sha, missing, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.GetCallersAsync(Routing, missing, depth: 1, limitPerLevel: 20, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Callers_CacheHit_ReturnsCached()
    {
        _store.GetReferencesAsync(Repo, Sha, Target, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        await _engine.GetCallersAsync(Routing, Target, depth: 1, limitPerLevel: 20, null);
        await _engine.GetCallersAsync(Routing, Target, depth: 1, limitPerLevel: 20, null);

        await _store.Received(1).GetReferencesAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<SymbolId>(),
            Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callers_DepthClamped_ToMaxDepth()
    {
        _store.GetReferencesAsync(Repo, Sha, Target, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        // maxDepth hard cap is 6, passing 100 should be clamped
        var result = await _engine.GetCallersAsync(
            Routing, Target, depth: 100, limitPerLevel: 20,
            new BudgetLimits(maxDepth: 6));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Callers_ExternalSymbols_FallbackDisplayName()
    {
        var external = SymbolId.From("M:System.Console.WriteLine");
        _store.GetReferencesAsync(Repo, Sha, Target, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, external, File1, 5, 5, null)]);
        // External symbol not in index
        _store.GetSymbolAsync(Repo, Sha, external, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.GetCallersAsync(Routing, Target, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        var extNode = result.Value.Data.Nodes.FirstOrDefault(n => n.SymbolId == external);
        extNode.Should().NotBeNull();
        extNode!.DisplayName.Should().Be(external.Value); // fallback = raw ID
        extNode.FilePath.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(SymbolId id) =>
        SymbolCard.CreateMinimal(id, id.Value, SymbolKind.Method, "void Method()",
            "MyNs", File1, 5, 20, "public", Confidence.High);
}
