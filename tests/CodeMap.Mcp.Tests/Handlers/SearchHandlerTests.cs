namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class SearchHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    private readonly ResponseEnvelope<SymbolSearchResponse> _fakeEnvelope;

    public SearchHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _fakeEnvelope = new ResponseEnvelope<SymbolSearchResponse>(
            Answer: "Found 1 result.",
            Data: new SymbolSearchResponse(
                Hits: [],
                TotalCount: 0,
                Truncated: false),
            Evidence: [],
            NextActions: [],
            Confidence: Core.Enums.Confidence.High,
            Meta: new ResponseMeta(
                Timing: new TimingBreakdown(TotalMs: 1.0),
                BaselineCommitSha: CommitSha.From(ValidSha),
                LimitsApplied: new Dictionary<string, LimitApplied>(),
                TokensSaved: 0,
                CostAvoided: 0m));

        _queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(_fakeEnvelope));

        _handler = new McpToolHandlers(_queryEngine, _git, NullLogger<McpToolHandlers>.Instance);
    }

    [Fact]
    public async Task Search_ValidQuery_DelegatesToQueryEngine()
    {
        var result = await _handler.HandleSearchAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["query"] = "OrderService" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).SearchSymbolsAsync(
            Arg.Any<RoutingContext>(), "OrderService",
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_WithFilters_BuildsCorrectFilters()
    {
        SymbolSearchFilters? capturedFilters = null;
        _queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Do<SymbolSearchFilters?>(f => capturedFilters = f),
                Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(_fakeEnvelope));

        await _handler.HandleSearchAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["query"] = "q",
                ["namespace"] = "MyNs",
                ["kinds"] = new JsonArray("Class", "Method"),
            },
            CancellationToken.None);

        capturedFilters.Should().NotBeNull();
        capturedFilters!.Namespace.Should().Be("MyNs");
        capturedFilters.Kinds.Should().ContainInOrder(
            Core.Enums.SymbolKind.Class, Core.Enums.SymbolKind.Method);
    }

    [Fact]
    public async Task Search_MissingQuery_ReturnsInvalidArgument()
    {
        var result = await _handler.HandleSearchAsync(
            new JsonObject { ["repo_path"] = RepoPath },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("query");
    }

    [Fact]
    public async Task Search_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleSearchAsync(
            new JsonObject { ["query"] = "foo" },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Search_FilePathFilter_PassedToEngine()
    {
        SymbolSearchFilters? capturedFilters = null;
        _queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Do<SymbolSearchFilters?>(f => capturedFilters = f),
                Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(_fakeEnvelope));

        var result = await _handler.HandleSearchAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["query"] = "handler",
                ["file_path"] = "src/",
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        capturedFilters.Should().NotBeNull();
        capturedFilters!.FilePath.Should().Be("src/");
    }

    [Fact]
    public async Task Search_NoFilePathFilter_FiltersIsNull()
    {
        SymbolSearchFilters? capturedFilters = null;
        _queryEngine.SearchSymbolsAsync(
                Arg.Any<RoutingContext>(), Arg.Any<string>(),
                Arg.Do<SymbolSearchFilters?>(f => capturedFilters = f),
                Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(_fakeEnvelope));

        await _handler.HandleSearchAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["query"] = "service" },
            CancellationToken.None);

        // No file_path, no namespace, no kinds → filters is null (no-op)
        capturedFilters.Should().BeNull();
    }

    [Fact]
    public void Search_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.RegisterQueryTools(registry);
        registry.Find("symbols.search").Should().NotBeNull();
    }
}
