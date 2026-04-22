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

public sealed class TypeHierarchyHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SymbolIdStr = "T:MyNs.MyClass";
    private const string WsIdStr = "ws-hierarchy-001";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly TypeHierarchyHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("hierarchy-test-repo");
    private static readonly SymbolId Target = SymbolId.From(SymbolIdStr);

    public TypeHierarchyHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.GetTypeHierarchyAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(
                       MakeHierarchyEnvelope(Target))));

        _handler = new TypeHierarchyHandler(_engine, _git, new McpSymbolResolver(_engine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<TypeHierarchyHandler>.Instance);
    }

    [Fact]
    public async Task Hierarchy_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
        await _engine.Received(1).GetTypeHierarchyAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hierarchy_WithWorkspaceId_SetsWorkspaceConsistency()
    {
        RoutingContext? captured = null;
        _engine.GetTypeHierarchyAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(
                       Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Success(
                           MakeHierarchyEnvelope(Target)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Workspace);
        captured.WorkspaceId!.Value.Value.Should().Be(WsIdStr);
    }

    [Fact]
    public async Task Hierarchy_MissingSymbolId_ReturnsInvalidArgument()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };
        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbol_id");
    }

    [Fact]
    public async Task Hierarchy_EngineError_ReturnsMcpError()
    {
        _engine.GetTypeHierarchyAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>.Failure(
                       CodeMapError.NotFound("Symbol", SymbolIdStr))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ResponseEnvelope<TypeHierarchyResponse> MakeHierarchyEnvelope(SymbolId target)
    {
        var data = new TypeHierarchyResponse(target, null, [], []);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<TypeHierarchyResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
