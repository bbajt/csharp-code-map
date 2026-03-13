namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class QueryEngineFindRefsTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly SymbolId SymId = SymbolId.From("M:MyService.DoWork");
    private static readonly FilePath File1 = FilePath.From("src/Caller.cs");

    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);
    private static readonly RoutingContext NoShaRouting = new(Repo);

    public QueryEngineFindRefsTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
        _store.GetSymbolAsync(Repo, Sha, SymId, Arg.Any<CancellationToken>())
              .Returns(MakeCard(SymId));
    }

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FindRefs_ExistingSymbol_ReturnsReferences()
    {
        _store.GetReferencesAsync(Repo, Sha, SymId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(MakeRefs(3));

        var result = await _engine.FindReferencesAsync(Routing, SymId, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(3);
        result.Value.Data.TargetSymbol.Should().Be(SymId);
    }

    [Fact]
    public async Task FindRefs_WithKindFilter_FiltersCorrectly()
    {
        _store.GetReferencesAsync(Repo, Sha, SymId, RefKind.Call, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(MakeRefs(2, RefKind.Call));

        var result = await _engine.FindReferencesAsync(Routing, SymId, RefKind.Call, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(2);
        result.Value.Data.References.Should().AllSatisfy(r => r.Kind.Should().Be(RefKind.Call));
    }

    [Fact]
    public async Task FindRefs_SymbolNotFound_ReturnsNotFoundError()
    {
        var missing = SymbolId.From("T:Missing");
        _store.GetSymbolAsync(Repo, Sha, missing, Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var result = await _engine.FindReferencesAsync(Routing, missing, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FindRefs_NoRefs_ReturnsEmptyList()
    {
        _store.GetReferencesAsync(Repo, Sha, SymId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new List<StoredReference>());

        var result = await _engine.FindReferencesAsync(Routing, SymId, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().BeEmpty();
        result.Value.Data.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task FindRefs_ExceedsLimit_Truncates()
    {
        var budgets = new BudgetLimits(maxReferences: 2);
        _store.GetReferencesAsync(Repo, Sha, SymId, null, 3, Arg.Any<CancellationToken>())
              .Returns(MakeRefs(3)); // return 3 (> limit 2)

        var result = await _engine.FindReferencesAsync(Routing, SymId, null, budgets);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Truncated.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(2);
    }

    [Fact]
    public async Task FindRefs_CacheHit_ReturnsCached()
    {
        _store.GetReferencesAsync(Repo, Sha, SymId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(MakeRefs(2));

        // First call populates cache
        await _engine.FindReferencesAsync(Routing, SymId, null, null);

        // Second call should hit cache
        var result = await _engine.FindReferencesAsync(Routing, SymId, null, null);

        result.IsSuccess.Should().BeTrue();
        await _store.Received(1).GetReferencesAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindRefs_IncludesExcerpts()
    {
        _store.GetReferencesAsync(Repo, Sha, SymId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(MakeRefs(1));
        _store.GetFileSpanAsync(Repo, Sha, File1, 10, 10, Arg.Any<CancellationToken>())
              .Returns(new FileSpan(File1, 10, 10, 100, "    var result = service.DoWork();", false));

        var result = await _engine.FindReferencesAsync(Routing, SymId, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References[0].Excerpt.Should().Be("var result = service.DoWork();");
    }

    [Fact]
    public async Task FindRefs_BaselineNotExists_ReturnsError()
    {
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _engine.FindReferencesAsync(Routing, SymId, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(SymbolId id) =>
        SymbolCard.CreateMinimal(id, id.Value, SymbolKind.Method, "void DoWork()",
            "MyNs", File1, 5, 20, "public", Confidence.High);

    private static IReadOnlyList<StoredReference> MakeRefs(int count, RefKind kind = RefKind.Call)
    {
        return Enumerable.Range(0, count)
            .Select(i => new StoredReference(
                kind,
                SymbolId.From($"M:Caller.Method{i}"),
                File1,
                10 + i,
                10 + i,
                null))
            .ToList();
    }
}
