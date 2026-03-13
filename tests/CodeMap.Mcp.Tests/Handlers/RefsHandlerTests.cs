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

public sealed class RefsHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SymbolId = "M:MyService.DoWork";
    private const string WsIdStr = "ws-refs-001";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly RefsHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("test-repo");

    public RefsHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        // Default: engine returns success with empty FindRefsResponse
        _engine.FindReferencesAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<RefKind?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(MakeRefsEnvelope())));

        _handler = new RefsHandler(_engine, _git, NullLogger<RefsHandler>.Instance);
    }

    [Fact]
    public async Task FindRefs_ValidParams_DelegatesToQueryEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
        };
        var result = await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _engine.Received(1).FindReferencesAsync(
            Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
            null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindRefs_WithKindFilter_ParsesRefKind()
    {
        RefKind? capturedKind = null;
        _engine.FindReferencesAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<RefKind?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedKind = ci.ArgAt<RefKind?>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(MakeRefsEnvelope()));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
            ["kind"] = "Call",
        };
        await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        capturedKind.Should().Be(RefKind.Call);
    }

    [Fact]
    public async Task FindRefs_WithLimit_PassesBudgets()
    {
        BudgetLimits? capturedBudgets = null;
        _engine.FindReferencesAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<RefKind?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedBudgets = ci.ArgAt<BudgetLimits?>(3);
                   return Task.FromResult(
                       Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(MakeRefsEnvelope()));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
            ["limit"] = 10,
        };
        await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        capturedBudgets.Should().NotBeNull();
        capturedBudgets!.MaxReferences.Should().Be(10);
    }

    [Fact]
    public async Task FindRefs_WithWorkspaceId_SetsWorkspaceConsistency()
    {
        RoutingContext? captured = null;
        _engine.FindReferencesAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<RefKind?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(
                       Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Success(MakeRefsEnvelope()));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Workspace);
        captured.WorkspaceId!.Value.Value.Should().Be(WsIdStr);
    }

    [Fact]
    public async Task FindRefs_MissingSymbolId_ReturnsInvalidArgument()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };
        var result = await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbol_id");
    }

    [Fact]
    public async Task FindRefs_InvalidKind_ReturnsInvalidArgument()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
            ["kind"] = "NotARealKind",
        };
        var result = await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task FindRefs_EngineReturnsError_ReturnsMcpError()
    {
        _engine.FindReferencesAsync(Arg.Any<RoutingContext>(), Arg.Any<Core.Types.SymbolId>(),
                    Arg.Any<RefKind?>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>.Failure(
                       CodeMapError.NotFound("Symbol", SymbolId))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolId,
        };
        var result = await _handler.HandleFindRefsAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ResponseEnvelope<FindRefsResponse> MakeRefsEnvelope()
    {
        var data = new FindRefsResponse(Core.Types.SymbolId.From(SymbolId), [], 0, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<FindRefsResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
