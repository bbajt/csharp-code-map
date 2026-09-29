namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Verifies the usage hints that ride along in successful responses
/// (empty-search nudge, Type-card member pointer) and error responses
/// (did-you-mean tool / method, fuzzy NOT_FOUND candidates). Each hint
/// targets a specific dead-end agents repeatedly hit; the tests pin both
/// the trigger condition and the textual content the agent will read.
/// </summary>
public sealed class UsageHintsTests
{
    // ── Pure helpers in HandlerHelpers ─────────────────────────────────────────

    [Theory]
    [InlineData("symbol.search", "symbols.search")]    // missing 's' typo
    [InlineData("symbols.serach", "symbols.search")]   // transposition
    [InlineData("graph.caller", "graph.callers")]      // missing trailing 's'
    [InlineData("symbols_search", "symbols.search")]   // separator confusion
    public void ClosestName_PicksClosestMatch(string requested, string expected)
    {
        var registered = new List<string>
        {
            "symbols.search", "symbols.get_card", "graph.callers", "graph.callees",
            "code.search_text", "codemap.guide", "index.ensure_baseline",
        };
        var result = typeof(HandlerHelpers).GetMethod("ClosestName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [requested, registered]);
        result.Should().Be(expected);
    }

    [Fact]
    public void ClosestName_TooFarAway_ReturnsNull()
    {
        var registered = new List<string> { "symbols.search", "codemap.guide" };
        var result = typeof(HandlerHelpers).GetMethod("ClosestName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["totally_unrelated_xyz", registered]);
        result.Should().BeNull("Levenshtein cap prevents nonsense suggestions");
    }

    [Theory]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Interface)]
    [InlineData(SymbolKind.Struct)]
    [InlineData(SymbolKind.Record)]
    [InlineData(SymbolKind.Enum)]
    public void TypeCardHint_TypeKinds_ProducesHint(SymbolKind kind)
    {
        var card = MakeCard(kind, "T:MyApp.Foo", "src/MyApp/Foo.cs");
        var hint = (string?)typeof(HandlerHelpers).GetMethod("TypeCardHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [card]);
        hint.Should().NotBeNull();
        hint.Should().Contain("symbols.search");
        hint.Should().Contain("types.hierarchy");
        hint.Should().Contain("src/MyApp/Foo.cs");
    }

    [Fact]
    public void TypeCardHint_Method_ReturnsNull()
    {
        var card = MakeCard(SymbolKind.Method, "M:MyApp.Foo.Bar", "src/MyApp/Foo.cs");
        var hint = (string?)typeof(HandlerHelpers).GetMethod("TypeCardHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [card]);
        hint.Should().BeNull("the hint only applies to Type symbols");
    }

    [Fact]
    public void TypeCardHint_UnknownFilePath_ReturnsNull()
    {
        // syntactic-fallback symbols have file_path="unknown"; the hint suggests a
        // file_path filter which would be useless in that case.
        var card = MakeCard(SymbolKind.Class, "T:MyApp.Ghost", "unknown");
        var hint = (string?)typeof(HandlerHelpers).GetMethod("TypeCardHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [card]);
        hint.Should().BeNull();
    }

    [Theory]
    [InlineData(null, false, "kinds")]                  // no query, no filters → suggest kinds-browse
    [InlineData("Foo", false, "code.search_text")]      // query only → suggest text fallback / wildcard
    [InlineData("Foo", true, "drop them")]              // filters present → suggest relaxation
    public void EmptySearchHint_ShapeDependsOnInputs(string? query, bool hasFilters, string expectedSubstring)
    {
        var hint = (string)typeof(HandlerHelpers).GetMethod("EmptySearchHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [query, hasFilters])!;
        hint.Should().Contain(expectedSubstring);
        hint.Should().StartWith("0 hits.");
    }

    [Fact]
    public void EmptySearchHint_WithFilters_DoesNotSayNameFilter()
    {
        // Regression: the with-filters hint must NOT mention "name_filter" because
        // symbols.search's filters are top-level fields, not nested under a
        // name_filter object. The earlier wording misled agents about WHERE to
        // remove the filter.
        var hint = (string)typeof(HandlerHelpers).GetMethod("EmptySearchHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["Foo", true])!;
        hint.Should().NotContain("name_filter");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Foo", false)]
    [InlineData("Restore Output", false)]
    [InlineData("Foo", true)]
    public void EmptySearchHint_DescribesTheRealSyntax(string? query, bool hasFilters)
    {
        // PHASE-21-08: the engine isn't FTS5 (removed in v2.1.0); the hint suggested FTS5 forms such
        // as `"Restore Output"*` that the v2 engine can't match.
        var hint = (string)typeof(HandlerHelpers).GetMethod("EmptySearchHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [query, hasFilters])!;
        hint.Should().NotContain("FTS5");
        hint.Should().NotContain("\"*");
    }

    [Fact]
    public void EmptySearchHint_MultiWordQuery_SuggestsOr()
    {
        var hint = (string)typeof(HandlerHelpers).GetMethod("EmptySearchHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["Restore Output", false])!;
        hint.Should().Contain("Restore OR Output");
    }

    // ── End-to-end: handlers actually wire the hints ───────────────────────────

    [Fact]
    public async Task HandleSearch_ZeroHits_InjectsEmptyHintIntoAnswer()
    {
        var handler = BuildHandler(out var queryEngine);
        var zeroHits = MakeSearchEnvelope([]);
        queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(zeroHits));

        var result = await handler.HandleSearchAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["query"] = "NoSuchThing" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var answer = JsonNode.Parse(result.Content)!["answer"]!.GetValue<string>();
        answer.Should().Contain("0 hits.",
            "agents reading the answer must see the dead-end is recoverable");
    }

    [Fact]
    public async Task HandleSearch_WithHits_DoesNotInjectHint()
    {
        var handler = BuildHandler(out var queryEngine);
        var withHits = MakeSearchEnvelope([new SymbolSearchHit(
            SymbolId.From("T:MyApp.Foo"), "global::MyApp.Foo", SymbolKind.Class,
            "public class Foo", null, FilePath.From("src/Foo.cs"), 1, 100)]);
        queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(withHits));

        var result = await handler.HandleSearchAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["query"] = "Foo" },
            CancellationToken.None);

        var answer = JsonNode.Parse(result.Content)!["answer"]!.GetValue<string>();
        answer.Should().NotContain("0 hits.", "the hint must NOT fire on non-empty results");
    }

    [Fact]
    public async Task HandleGetCard_TypeSymbol_IncludeCodeFalse_StillEmitsHint()
    {
        // Regression: an earlier version of AppendSourceCodeAsync placed the
        // TypeCardHint AFTER the include_code=false early return, so metadata-only
        // calls (include_code: false) silently lost the hint. The hint must fire
        // on every return path.
        var handler = BuildHandler(out var queryEngine);
        var card = MakeCard(SymbolKind.Class, "T:MyApp.Foo", "src/MyApp/Foo.cs");
        var envelope = new ResponseEnvelope<SymbolCard>(
            Answer: "Class MyApp.Foo",
            Data: card, Evidence: [], NextActions: [], Confidence: Confidence.High,
            Meta: new ResponseMeta(
                Timing: new TimingBreakdown(TotalMs: 1.0),
                BaselineCommitSha: CommitSha.From(ValidSha),
                LimitsApplied: new Dictionary<string, LimitApplied>(),
                TokensSaved: 0, CostAvoided: 0m));
        queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope));

        var result = await handler.HandleGetCardAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["symbol_id"] = "T:MyApp.Foo",
                ["include_code"] = false,
            },
            CancellationToken.None);

        var answer = JsonNode.Parse(result.Content)!["answer"]!.GetValue<string>();
        answer.Should().Contain("symbols.search",
            "the Type-card hint must fire even when include_code=false skips the source-fetch branch");
        answer.Should().Contain("types.hierarchy");
    }

    [Fact]
    public async Task HandleGetCard_NotFound_RunsFuzzySearchAndInlinesCandidateIds()
    {
        var handler = BuildHandler(out var queryEngine);
        // First call: explicit symbol_id NOT_FOUND
        queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                new CodeMapError(ErrorCodes.NotFound, "Symbol not found.")));
        // Fuzzy fallback: a single candidate found by simple name
        var candidate = new SymbolSearchHit(
            SymbolId.From("M:MyApp.Foo.Bar"), "global::MyApp.Foo.Bar", SymbolKind.Method,
            "public void Bar()", null, FilePath.From("src/Foo.cs"), 42, 100);
        queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), "Bar",
                Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(
                MakeSearchEnvelope([candidate])));

        var result = await handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "M:MyApp.Wrong.Bar" },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        var err = JsonNode.Parse(result.Content)!;
        var message = err["message"]!.GetValue<string>();
        message.Should().Contain("M:MyApp.Foo.Bar",
            "the error must inline the real symbol_id so the agent can retry directly");
        message.Should().Contain("src/Foo.cs:42",
            "candidate location helps the agent disambiguate when there are multiple");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static McpToolHandlers BuildHandler(out IQueryEngine queryEngine)
    {
        queryEngine = Substitute.For<IQueryEngine>();
        var git = Substitute.For<IGitService>();
        git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));
        return new McpToolHandlers(
            queryEngine, git, new McpSymbolResolver(queryEngine),
            new RepoRegistry(), new WorkspaceStickyRegistry(),
            NullLogger<McpToolHandlers>.Instance);
    }

    private static SymbolCard MakeCard(SymbolKind kind, string symbolId, string filePath) =>
        SymbolCard.CreateMinimal(SymbolId.From(symbolId), "global::" + symbolId[2..],
            kind, "public " + kind.ToString().ToLowerInvariant(), "MyApp",
            FilePath.From(filePath), 1, 10, "public", Confidence.High);

    private static ResponseEnvelope<SymbolSearchResponse> MakeSearchEnvelope(IReadOnlyList<SymbolSearchHit> hits) =>
        new(
            Answer: hits.Count > 0 ? $"Found {hits.Count} result(s)." : "Found 0 results.",
            Data: new SymbolSearchResponse(Hits: hits, TotalCount: hits.Count, Truncated: false),
            Evidence: [],
            NextActions: [],
            Confidence: Confidence.High,
            Meta: new ResponseMeta(
                Timing: new TimingBreakdown(TotalMs: 1.0),
                BaselineCommitSha: CommitSha.From(ValidSha),
                LimitsApplied: new Dictionary<string, LimitApplied>(),
                TokensSaved: 0,
                CostAvoided: 0m));
}
