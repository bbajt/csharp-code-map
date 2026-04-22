namespace CodeMap.Mcp.Tests.Handlers;

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

/// <summary>
/// Unit tests for McpToolHandlers.HandleGetCardAsync stable_id routing (PHASE-03-01).
/// Verifies that sym_ prefixed IDs are dispatched to GetSymbolByStableIdAsync
/// and plain FQN IDs continue to use GetSymbolCardAsync.
/// </summary>
public sealed class CardHandlerStableIdTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    private static readonly StableId Stable = new("sym_" + new string('c', 16));

    public CardHandlerStableIdTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _handler = new McpToolHandlers(_queryEngine, _git, new McpSymbolResolver(_queryEngine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<McpToolHandlers>.Instance);
    }

    [Fact]
    public async Task GetCard_StableIdPrefix_DispatchesToGetSymbolByStableIdAsync()
    {
        var card = MakeCard();
        _queryEngine.GetSymbolByStableIdAsync(
                Arg.Any<RoutingContext>(), Arg.Any<StableId>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(MakeEnvelope(card)));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = Stable.Value },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetSymbolByStableIdAsync(
            Arg.Any<RoutingContext>(),
            Arg.Is<StableId>(s => s.Value == Stable.Value),
            Arg.Any<CancellationToken>());
        await _queryEngine.DidNotReceive().GetSymbolCardAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_FqnId_DispatchesToGetSymbolCardAsync()
    {
        var card = MakeCard();
        _queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(MakeEnvelope(card)));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "MyNs.MyClass" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetSymbolCardAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
        await _queryEngine.DidNotReceive().GetSymbolByStableIdAsync(
            Arg.Any<RoutingContext>(), Arg.Any<StableId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_StableIdPrefix_NotFound_ReturnsError()
    {
        _queryEngine.GetSymbolByStableIdAsync(
                Arg.Any<RoutingContext>(), Arg.Any<StableId>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", Stable.Value)));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = Stable.Value },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ─── Factory ─────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard() =>
        SymbolCard.CreateMinimal(
            SymbolId.From("MyNs.MyClass"), "MyNs.MyClass", SymbolKind.Class,
            "public class MyClass", "MyNs",
            FilePath.From("src/MyClass.cs"), 1, 10, "public", Confidence.High);

    private static ResponseEnvelope<SymbolCard> MakeEnvelope(SymbolCard card) =>
        new ResponseEnvelope<SymbolCard>(
            "Got card.", card, [], [], Confidence.High,
            new ResponseMeta(
                new TimingBreakdown(1.0), CommitSha.From(ValidSha),
                new Dictionary<string, LimitApplied>(), 0, 0m));
}
