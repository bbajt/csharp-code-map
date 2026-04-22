namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
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

/// <summary>Tests for symbol ID auto-correct in HandleGetCardAsync (PHASE-09-01).</summary>
public sealed class CardHandlerAutoCorrectTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    public CardHandlerAutoCorrectTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _handler = new McpToolHandlers(_queryEngine, _git, new McpSymbolResolver(_queryEngine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<McpToolHandlers>.Instance);
    }

    private static ResponseEnvelope<SymbolCard> MakeEnvelope(SymbolId symbolId) =>
        new("Got card.",
            SymbolCard.CreateMinimal(symbolId, "MyClass", SymbolKind.Class,
                "public class MyClass", "MyNs",
                FilePath.From("src/MyClass.cs"), 0, 0, "public", Confidence.High),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
                new Dictionary<string, LimitApplied>(), 0, 0m));

    [Fact]
    public async Task GetCard_MissingTypePrefix_AutoCorrectsToTPrefix()
    {
        var rawId = "MyNs.MyClass";
        var correctedId = SymbolId.From("T:" + rawId);
        var envelope = MakeEnvelope(correctedId);

        // No prefix → fails
        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(),
                SymbolId.From(rawId), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", rawId)));

        // T: prefix → succeeds
        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(),
                correctedId, Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["symbol_id"] = rawId,
                ["include_code"] = false,
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonNode.Parse(result.Content)!.AsObject();
        json["answer"]!.GetValue<string>().Should().Contain("auto-corrected");
        json["answer"]!.GetValue<string>().Should().Contain("T:" + rawId);
    }

    [Fact]
    public async Task GetCard_MissingPrefix_TriesPrefixesInOrder_ReturnsFirstMatch()
    {
        var rawId = "MyNs.MyMethod";
        // T: fails, M: succeeds
        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(),
                SymbolId.From("T:" + rawId), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", rawId)));

        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(),
                SymbolId.From("M:" + rawId), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                MakeEnvelope(SymbolId.From("M:" + rawId))));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["symbol_id"] = rawId,
                ["include_code"] = false,
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonNode.Parse(result.Content)!.AsObject();
        json["answer"]!.GetValue<string>().Should().Contain("M:" + rawId);
    }

    [Fact]
    public async Task GetCard_AlreadyHasTPrefix_NoAutoCorrect()
    {
        var symbolIdStr = "T:MyNs.MyClass";
        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(),
                SymbolId.From(symbolIdStr), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", symbolIdStr)));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = symbolIdStr },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        // No auto-correct was attempted — only one call to GetSymbolCardAsync
        await _queryEngine.Received(1).GetSymbolCardAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_AllPrefixesFail_ReturnsNotFoundSuggestion()
    {
        var rawId = "NoSuchThing";
        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(),
                Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", rawId)));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = rawId },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbols.search");
    }
}
