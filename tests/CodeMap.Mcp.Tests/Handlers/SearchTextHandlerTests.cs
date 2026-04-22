namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>Tests for code.search_text handler (PHASE-09-02).</summary>
public sealed class SearchTextHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    public SearchTextHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _handler = new McpToolHandlers(_queryEngine, _git, new McpSymbolResolver(_queryEngine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<McpToolHandlers>.Instance);
    }

    private static ResponseEnvelope<SearchTextResponse> MakeEnvelope(
        string pattern, List<TextMatch> matches, bool truncated = false) =>
        new($"Found {matches.Count} matches.",
            new SearchTextResponse(pattern, matches, 5, truncated),
            [], [], Core.Enums.Confidence.High,
            new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
                new Dictionary<string, LimitApplied>(), 0, 0m));

    [Fact]
    public async Task HandleSearchText_ValidArgs_DelegatesToQueryEngine()
    {
        var matches = new List<TextMatch>
        {
            new(FilePath.From("src/Foo.cs"), 10, "var x = new OrderService();"),
        };
        _queryEngine.SearchTextAsync(Arg.Any<RoutingContext>(), "OrderService",
                Arg.Any<string?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Success(
                MakeEnvelope("OrderService", matches)));

        var result = await _handler.HandleSearchTextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["pattern"] = "OrderService" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).SearchTextAsync(
            Arg.Any<RoutingContext>(), "OrderService",
            Arg.Any<string?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSearchText_MissingPattern_ReturnsInvalidArg()
    {
        var result = await _handler.HandleSearchTextAsync(
            new JsonObject { ["repo_path"] = RepoPath },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("pattern is required");
    }

    [Fact]
    public async Task HandleSearchText_MissingRepoPath_ReturnsInvalidArg()
    {
        var result = await _handler.HandleSearchTextAsync(
            new JsonObject { ["pattern"] = "Order" },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("repo_path is required");
    }

    [Fact]
    public async Task HandleSearchText_LimitClamped_MaxIs200()
    {
        _queryEngine.SearchTextAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Success(
                MakeEnvelope("x", [])));

        await _handler.HandleSearchTextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["pattern"] = "x", ["limit"] = 9999 },
            CancellationToken.None);

        await _queryEngine.Received(1).SearchTextAsync(
            Arg.Any<RoutingContext>(), Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<BudgetLimits?>(b => b != null && b.MaxResults == 200),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSearchText_DefaultLimit_Is50()
    {
        _queryEngine.SearchTextAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Success(
                MakeEnvelope("x", [])));

        await _handler.HandleSearchTextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["pattern"] = "x" },
            CancellationToken.None);

        await _queryEngine.Received(1).SearchTextAsync(
            Arg.Any<RoutingContext>(), Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<BudgetLimits?>(b => b != null && b.MaxResults == 50),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSearchText_FilePathFilter_PassedThrough()
    {
        _queryEngine.SearchTextAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>.Success(
                MakeEnvelope("x", [])));

        await _handler.HandleSearchTextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["pattern"] = "x", ["file_path"] = "src/" },
            CancellationToken.None);

        await _queryEngine.Received(1).SearchTextAsync(
            Arg.Any<RoutingContext>(), Arg.Any<string>(),
            "src/",
            Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }
}
