namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
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

/// <summary>
/// Tests that workspace-mode parameters flow through MCP handlers correctly.
/// Verifies that workspace_id is picked up and workspace-mode queries no longer
/// return the temporary PHASE-02-02 error.
/// </summary>
public sealed class WorkspaceModeQueryTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WsIdStr = "ws-test-001";
    private const string SymbolId = "T:OrderService";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(ValidSha);

    public WorkspaceModeQueryTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        // Default: engine returns success with empty search response
        _engine.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                    Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>.Success(
                   MakeSearchEnvelope())));
        _engine.GetSymbolCardAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                   MakeCardEnvelope())));
        _engine.GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(), Arg.Any<int>(), Arg.Any<int>(),
                    Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(
                   MakeSpanEnvelope())));
        _engine.GetDefinitionSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(
                   MakeSpanEnvelope())));

        _handler = new McpToolHandlers(_engine, _git, NullLogger<McpToolHandlers>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_WithWorkspaceId_SetsWorkspaceConsistency()
    {
        RoutingContext? captured = null;
        _engine.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                    Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                       .Success(MakeSearchEnvelope()));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["query"] = "OrderService",
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleSearchAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Workspace);
        captured.WorkspaceId.Should().NotBeNull();
        captured.WorkspaceId!.Value.Value.Should().Be(WsIdStr);
    }

    [Fact]
    public async Task Search_WithoutWorkspaceId_SetsCommittedConsistency()
    {
        RoutingContext? captured = null;
        _engine.SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
                    Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>
                       .Success(MakeSearchEnvelope()));
               });

        var args = new JsonObject { ["repo_path"] = RepoPath, ["query"] = "OrderService" };
        await _handler.HandleSearchAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Committed);
        captured.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public async Task GetCard_WithWorkspaceId_SetsWorkspaceConsistency()
    {
        RoutingContext? captured = null;
        _engine.GetSymbolCardAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(Result<ResponseEnvelope<SymbolCard>, CodeMapError>
                       .Success(MakeCardEnvelope()));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleGetCardAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Workspace);
    }

    [Fact]
    public async Task GetSpan_WithWorkspaceId_PassesThroughToEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["file_path"] = "src/OrderService.cs",
            ["start_line"] = 1,
            ["end_line"] = 10,
            ["workspace_id"] = WsIdStr,
        };
        var result = await _handler.HandleGetSpanAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _engine.Received(1).GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDefinitionSpan_WithWorkspaceId_SetsWorkspaceConsistency()
    {
        RoutingContext? captured = null;
        _engine.GetDefinitionSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(Result<ResponseEnvelope<SpanResponse>, CodeMapError>
                       .Success(MakeSpanEnvelope()));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleGetDefinitionSpanAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Workspace);
    }

    [Fact]
    public async Task Search_WorkspaceMode_NoLongerReturnsTemporaryError()
    {
        // Pre-PHASE-02-03: workspace_id returned INDEX_NOT_AVAILABLE.
        // After this phase, it passes through to the engine.
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["query"] = "OrderService",
            ["workspace_id"] = WsIdStr,
        };
        var result = await _handler.HandleSearchAsync(args, CancellationToken.None);

        // Engine is called (not short-circuited with temporary error)
        await _engine.Received(1).SearchSymbolsAsync(Arg.Any<RoutingContext>(), Arg.Any<string>(),
            Arg.Any<SymbolSearchFilters?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ResponseEnvelope<SymbolSearchResponse> MakeSearchEnvelope()
    {
        var data = new SymbolSearchResponse([], 0, false);
        var meta = new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolSearchResponse>("answer", data, [], [], Confidence.High, meta);
    }

    private static ResponseEnvelope<SymbolCard> MakeCardEnvelope()
    {
        var card = SymbolCard.CreateMinimal(
            Core.Types.SymbolId.From("T:Foo"), "Foo", SymbolKind.Class, "class Foo",
            "NS", FilePath.From("src/Foo.cs"), 1, 10, "public", Confidence.High);
        var meta = new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolCard>("answer", card, [], [], Confidence.High, meta);
    }

    private static ResponseEnvelope<SpanResponse> MakeSpanEnvelope()
    {
        var span = new SpanResponse(FilePath.From("src/A.cs"), 1, 10, 40, "// code", false);
        var meta = new ResponseMeta(new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SpanResponse>("answer", span, [], [], Confidence.High, meta);
    }
}
