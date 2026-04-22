namespace CodeMap.Mcp.Tests.Resolution;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Resolution;
using FluentAssertions;
using NSubstitute;

public sealed class McpSymbolResolverTests
{
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RoutingContext Routing() =>
        new(RepoId.From("r1"), baselineCommitSha: CommitSha.From(ValidSha));

    private static SymbolSearchHit Hit(string id, string signature = "stub()", string ns = "N") => new(
        SymbolId.From(id),
        FullyQualifiedName: id,
        Kind: SymbolKind.Method,
        Signature: signature,
        DocumentationSnippet: null,
        FilePath: FilePath.From("src/X.cs"),
        Line: 1,
        Score: 1.0);

    private static ResponseEnvelope<SymbolSearchResponse> Envelope(params SymbolSearchHit[] hits) =>
        new("ok", new SymbolSearchResponse(hits, hits.Length, Truncated: false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
                new Dictionary<string, LimitApplied>(), 0, 0m));

    private static IQueryEngine EngineReturning(params SymbolSearchHit[] hits)
    {
        var engine = Substitute.For<IQueryEngine>();
        engine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string?>(),
                Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(Envelope(hits)));
        return engine;
    }

    [Fact]
    public async Task ExplicitSymbolId_BypassesSearch()
    {
        var engine = Substitute.For<IQueryEngine>();
        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["symbol_id"] = "M:Foo.Bar" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Symbol!.Value.Value.Should().Be("M:Foo.Bar");
        await engine.DidNotReceive().SearchSymbolsAsync(
            Arg.Any<RoutingContext>(), Arg.Any<string?>(),
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NeitherSymbolIdNorName_ReturnsInvalidArgument()
    {
        var resolver = new McpSymbolResolver(Substitute.For<IQueryEngine>());
        var args = new JsonObject { };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.Symbol.Should().BeNull();
        result.Error!.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task EmptyName_ReturnsInvalidArgument()
    {
        var resolver = new McpSymbolResolver(Substitute.For<IQueryEngine>());
        var args = new JsonObject { ["name"] = "" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.Error!.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task NameWithSingleMatch_ResolvesToThatSymbol()
    {
        var engine = EngineReturning(Hit("M:Ns.Foo.DoIt"));
        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["name"] = "DoIt" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Symbol!.Value.Value.Should().Be("M:Ns.Foo.DoIt");
    }

    [Fact]
    public async Task NameWithZeroMatches_ReturnsNotFound()
    {
        var engine = EngineReturning(); // empty hits
        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["name"] = "Nonexistent" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.Symbol.Should().BeNull();
        result.Error!.Code.Should().Be(ErrorCodes.NotFound);
        result.Error!.Message.Should().Contain("Nonexistent");
    }

    [Fact]
    public async Task NameWithMultipleMatches_ReturnsAmbiguousWithCandidates()
    {
        var engine = EngineReturning(
            Hit("M:A.Foo.DoIt"),
            Hit("M:B.Foo.DoIt"),
            Hit("M:C.Foo.DoIt"));
        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["name"] = "DoIt" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.Symbol.Should().BeNull();
        result.Error!.Code.Should().Be(ErrorCodes.Ambiguous);
        result.Error!.Message.Should().Contain("M:A.Foo.DoIt");
        result.Error!.Message.Should().Contain("M:B.Foo.DoIt");
        result.Error!.Message.Should().Contain("M:C.Foo.DoIt");
        // Candidates list in Details
        result.Error!.Details.Should().ContainKey("candidates");
        var candidates = result.Error!.Details!["candidates"] as IReadOnlyList<string>;
        candidates.Should().NotBeNull();
        candidates!.Should().Contain(["M:A.Foo.DoIt", "M:B.Foo.DoIt", "M:C.Foo.DoIt"]);
    }

    [Fact]
    public async Task NameWithManyMatches_CapsCandidateList()
    {
        // Server returns MaxCandidates + 1 hits; the resolver message must cap display.
        var hits = Enumerable.Range(1, McpSymbolResolver.MaxCandidates + 1)
            .Select(i => Hit($"M:N.Cls{i}.DoIt"))
            .ToArray();
        var engine = EngineReturning(hits);
        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["name"] = "DoIt" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.Error!.Code.Should().Be(ErrorCodes.Ambiguous);
        // The displayed "N+ symbols" formatting uses "5+" when total > MaxCandidates
        result.Error!.Message.Should().Contain($"{McpSymbolResolver.MaxCandidates}+ symbols");
    }

    [Fact]
    public async Task NameFilter_ForwardedToSearch()
    {
        // Capture the filters passed into search to confirm name_filter parsing works.
        var engine = Substitute.For<IQueryEngine>();
        SymbolSearchFilters? captured = null;
        engine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string?>(),
                Arg.Do<SymbolSearchFilters?>(f => captured = f),
                Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(
                Envelope(Hit("M:Scoped.Foo.DoIt"))));

        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject
        {
            ["name"] = "DoIt",
            ["name_filter"] = new JsonObject
            {
                ["namespace"] = "Scoped",
                ["file_path"] = "src/",
                ["project_name"] = "MyApp",
                ["kinds"] = new JsonArray("Method"),
            },
        };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Namespace.Should().Be("Scoped");
        captured.FilePath.Should().Be("src/");
        captured.ProjectName.Should().Be("MyApp");
        captured.Kinds.Should().ContainSingle().Which.Should().Be(SymbolKind.Method);
    }

    [Fact]
    public async Task SearchFailure_PropagatesErrorFromQueryEngine()
    {
        var engine = Substitute.For<IQueryEngine>();
        var searchError = CodeMapError.IndexNotAvailable("r1", ValidSha);
        engine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string?>(),
                Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Failure(searchError));

        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["name"] = "DoIt" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.Symbol.Should().BeNull();
        result.Error!.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task ExplicitSymbolIdWinsOverName_WhenBothProvided()
    {
        // When both are present, symbol_id takes precedence. Search must NOT be invoked.
        var engine = Substitute.For<IQueryEngine>();
        var resolver = new McpSymbolResolver(engine);
        var args = new JsonObject { ["symbol_id"] = "M:Exact.One", ["name"] = "Ambiguous" };

        var result = await resolver.ResolveAsync(args, Routing(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Symbol!.Value.Value.Should().Be("M:Exact.One");
        await engine.DidNotReceive().SearchSymbolsAsync(
            Arg.Any<RoutingContext>(), Arg.Any<string?>(),
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
            Arg.Any<CancellationToken>());
    }
}
