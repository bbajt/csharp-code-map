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

public sealed class GraphHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SymbolIdStr = "M:MyNs.Service.DoWork";
    private const string WsIdStr = "ws-graph-001";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly GraphHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("graph-test-repo");
    private static readonly SymbolId Root = SymbolId.From(SymbolIdStr);

    public GraphHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        // Default: engine returns success with empty CallGraphResponse
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root))));

        _engine.GetCalleesAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root))));

        // Default: card lookup returns a method card (not a type) so type validation passes
        _engine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                       MakeCardEnvelope(SymbolKind.Method))));

        _handler = new GraphHandler(_engine, _git, new McpSymbolResolver(_engine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<GraphHandler>.Instance);
    }

    // ── Callers ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callers_ValidParams_DelegatesToQueryEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleCallersAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _engine.Received(1).GetCallersAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callers_DefaultDepth_UsesOne()
    {
        int capturedDepth = -1;
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedDepth = ci.ArgAt<int>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        await _handler.HandleCallersAsync(args, CancellationToken.None);

        capturedDepth.Should().Be(1);
    }

    [Fact]
    public async Task Callers_CustomDepth_PassedThrough()
    {
        int capturedDepth = -1;
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedDepth = ci.ArgAt<int>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
            ["depth"] = 3,
        };
        await _handler.HandleCallersAsync(args, CancellationToken.None);

        capturedDepth.Should().Be(3);
    }

    [Fact]
    public async Task Callers_WithWorkspaceId_SetsWorkspaceConsistency()
    {
        RoutingContext? captured = null;
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(
                       Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleCallersAsync(args, CancellationToken.None);

        captured!.Consistency.Should().Be(ConsistencyMode.Workspace);
        captured.WorkspaceId!.Value.Value.Should().Be(WsIdStr);
    }

    [Fact]
    public async Task Callers_MissingSymbolId_ReturnsInvalidArgument()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };
        var result = await _handler.HandleCallersAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbol_id");
    }

    [Fact]
    public async Task Callers_EngineReturnsError_ReturnsMcpError()
    {
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Failure(
                       CodeMapError.NotFound("Symbol", SymbolIdStr))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleCallersAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── Callees ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callees_ValidParams_DelegatesToQueryEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleCalleesAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _engine.Received(1).GetCalleesAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callees_DefaultDepth_UsesOne()
    {
        int capturedDepth = -1;
        _engine.GetCalleesAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedDepth = ci.ArgAt<int>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        await _handler.HandleCalleesAsync(args, CancellationToken.None);

        capturedDepth.Should().Be(1);
    }

    // ── Type symbol validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Interface)]
    [InlineData(SymbolKind.Struct)]
    [InlineData(SymbolKind.Record)]
    public async Task Callers_TypeSymbol_ReturnsHelpfulInvalidArgError(SymbolKind typeKind)
    {
        _engine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                       MakeCardEnvelope(typeKind))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleCallersAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("methods", "error must explain graph.callers works on methods not types");
    }

    [Theory]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Interface)]
    public async Task Callees_TypeSymbol_ReturnsHelpfulInvalidArgError(SymbolKind typeKind)
    {
        _engine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                       MakeCardEnvelope(typeKind))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleCalleesAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("methods");
    }

    [Fact]
    public async Task Callers_MethodSymbol_ProceedsToQueryEngine()
    {
        // Card returns method kind — validation passes, query engine is called
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };
        var result = await _handler.HandleCallersAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _engine.Received(1).GetCallersAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ResponseEnvelope<CallGraphResponse> MakeGraphEnvelope(SymbolId root)
    {
        var data = new CallGraphResponse(root, [], 0, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<CallGraphResponse>("answer", data, [], [], Confidence.High, meta);
    }

    [Fact]
    public async Task Callers_NotFound_SuggestsSearch()
    {
        // Arrange — card lookup returns NOT_FOUND (e.g. bad FQN)
        _engine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                    CodeMapError.NotFound("Symbol", SymbolIdStr))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };

        var result = await _handler.HandleCallersAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbols.search");
        result.Content.Should().Contain("DoWork");
    }

    [Fact]
    public async Task Callees_NotFound_SuggestsSearch()
    {
        _engine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Failure(
                    CodeMapError.NotFound("Symbol", SymbolIdStr))));

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
        };

        var result = await _handler.HandleCalleesAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbols.search");
        result.Content.Should().Contain("DoWork");
    }

    [Fact]
    public async Task Callers_DefaultFollowInterface_IsFalse()
    {
        bool captured = true;
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>(), Arg.Any<bool>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<bool>(6);
                   return Task.FromResult(
                       Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root)));
               });

        var args = new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr };
        await _handler.HandleCallersAsync(args, CancellationToken.None);

        captured.Should().BeFalse(because: "follow_interface defaults to false when absent from MCP args");
    }

    [Fact]
    public async Task Callers_FollowInterfaceTrue_PropagatesToQueryEngine()
    {
        bool captured = false;
        _engine.GetCallersAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>(), Arg.Any<bool>())
               .Returns(ci =>
               {
                   captured = ci.ArgAt<bool>(6);
                   return Task.FromResult(
                       Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>.Success(MakeGraphEnvelope(Root)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["symbol_id"] = SymbolIdStr,
            ["follow_interface"] = true,
        };
        await _handler.HandleCallersAsync(args, CancellationToken.None);

        captured.Should().BeTrue();
    }

    [Fact]
    public void Callers_Schema_AdvertisesFollowInterface()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        var tool = registry.Find("graph.callers")!;

        var props = tool.InputSchema["properties"]!.AsObject();
        props.ContainsKey("follow_interface").Should().BeTrue(
            because: "agents need to discover the union flag from the tool schema");
        var fi = props["follow_interface"]!.AsObject();
        fi["type"]!.GetValue<string>().Should().Be("boolean");
    }

    private static ResponseEnvelope<SymbolCard> MakeCardEnvelope(SymbolKind kind)
    {
        var card = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(SymbolIdStr),
            fullyQualifiedName: SymbolIdStr,
            kind: kind,
            signature: "void DoWork()",
            @namespace: "MyNs",
            filePath: Core.Types.FilePath.From("src/Service.cs"),
            spanStart: 1, spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<SymbolCard>("answer", card, [], [], Confidence.High, meta);
    }
}
