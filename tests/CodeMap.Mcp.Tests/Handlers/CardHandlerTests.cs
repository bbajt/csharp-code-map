namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class CardHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    public CardHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _handler = new McpToolHandlers(_queryEngine, _git, NullLogger<McpToolHandlers>.Instance);
    }

    [Fact]
    public async Task GetCard_ValidSymbolId_DelegatesToQueryEngine()
    {
        var card = SymbolCard.CreateMinimal(
            SymbolId.From("MyNs.MyClass"), "MyNs.MyClass", SymbolKind.Class,
            "public class MyClass", "MyNs",
            FilePath.From("src/MyClass.cs"), 1, 10, "public", Confidence.High);

        _queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), SymbolId.From("MyNs.MyClass"),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                new ResponseEnvelope<SymbolCard>(
                    "Got card.", card, [], [], Confidence.High,
                    new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
                        new Dictionary<string, LimitApplied>(), 0, 0m))));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "MyNs.MyClass" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetSymbolCardAsync(
            Arg.Any<RoutingContext>(),
            SymbolId.From("MyNs.MyClass"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_NotFound_ReturnsError()
    {
        _queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", "MyNs.Missing")));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "MyNs.Missing" },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetCard_NotFound_SuggestsSearch()
    {
        // Arrange — engine returns NOT_FOUND
        _queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", "M:MyNs.MyService.DoAsync")));

        // Act
        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "M:MyNs.MyService.DoAsync" },
            CancellationToken.None);

        // Assert — error contains suggestion with simple name
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbols.search");
        result.Content.Should().Contain("DoAsync");
    }

    [Theory]
    [InlineData("M:Ns.Cls.Method(System.String)", "Method")]
    [InlineData("T:Ns.Cls", "Cls")]
    [InlineData("M:Ns.Cls.Method", "Method")]
    [InlineData("DoAsync", "DoAsync")]
    public async Task GetCard_NotFound_SuggestsCorrectSimpleName(string symbolId, string expectedName)
    {
        _queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolId)));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = symbolId },
            CancellationToken.None);

        // The JSON content has the name embedded in symbols.search("...") — check name is present
        result.Content.Should().Contain(expectedName);
        result.Content.Should().Contain("symbols.search");
    }

    [Fact]
    public async Task GetCard_MissingSymbolId_ReturnsError()
    {
        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbol_id");
    }

    [Fact]
    public void GetCard_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.RegisterQueryTools(registry);
        registry.Find("symbols.get_card").Should().NotBeNull();
    }
}
