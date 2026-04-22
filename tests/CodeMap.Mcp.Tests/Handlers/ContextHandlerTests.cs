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

public sealed class ContextHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MethodId = "M:MyNs.MyService.DoAsync";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ContextHandler _handler;

    public ContextHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _handler = new ContextHandler(_queryEngine, _git, new McpSymbolResolver(_queryEngine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<ContextHandler>.Instance);
    }

    [Fact]
    public async Task Handler_ValidParams_DelegatesToEngine()
    {
        SetupContextSuccess();

        var result = await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            SymbolId.From(MethodId),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_DefaultCalleeDepth_Is1()
    {
        SetupContextSuccess();

        await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId },
            CancellationToken.None);

        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(),
            Arg.Is(1),
            Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_DefaultMaxCallees_Is10()
    {
        SetupContextSuccess();

        await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId },
            CancellationToken.None);

        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(),
            Arg.Any<int>(),
            Arg.Is(10),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_DefaultIncludeCode_IsTrue()
    {
        SetupContextSuccess();

        await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId },
            CancellationToken.None);

        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Is(true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_CalleeDepth0_PassedThrough()
    {
        SetupContextSuccess();

        await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId, ["callee_depth"] = 0 },
            CancellationToken.None);

        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(),
            Arg.Is(0),
            Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_IncludeCodeFalse_PassedThrough()
    {
        SetupContextSuccess();

        await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId, ["include_code"] = false },
            CancellationToken.None);

        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Is(false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_StableIdPrefix_ResolvesToSymbolId()
    {
        var stableId = new StableId("sym_aabbccddeeff0011");
        var resolvedCard = MakeCardEnvelope();
        var resolvedSymbolId = resolvedCard.Data.SymbolId;

        _queryEngine.GetSymbolByStableIdAsync(
                Arg.Any<RoutingContext>(), stableId, Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(resolvedCard));
        SetupContextSuccess(resolvedSymbolId);

        var result = await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "sym_aabbccddeeff0011" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetContextAsync(
            Arg.Any<RoutingContext>(),
            resolvedSymbolId,
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleGetContextAsync(
            new JsonObject { ["symbol_id"] = MethodId },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("repo_path");
    }

    [Fact]
    public async Task Handler_MissingSymbolId_ReturnsError()
    {
        var result = await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbol_id");
    }

    [Fact]
    public async Task Handler_NotFound_ReturnsError()
    {
        _queryEngine.GetContextAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", MethodId)));

        var result = await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void GetContext_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        registry.Find("symbols.get_context").Should().NotBeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupContextSuccess(SymbolId? symbolId = null)
    {
        var resolvedId = symbolId ?? SymbolId.From(MethodId);
        var card = SymbolCard.CreateMinimal(
            resolvedId, "MyNs.MyService.DoAsync", SymbolKind.Method,
            "public async Task DoAsync()", "MyNs",
            FilePath.From("src/MyService.cs"), 10, 20, "public", Confidence.High);

        var primary = new SymbolCardWithCode(card, "public async Task DoAsync() { }", false);
        var response = new SymbolContextResponse(primary, [], 0, "# MyNs.MyService.DoAsync");
        var meta = new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
            new Dictionary<string, LimitApplied>(), 0, 0m);
        var envelope = new ResponseEnvelope<SymbolContextResponse>(
            "Context for 'MyNs.MyService.DoAsync': card + code (no callees found).",
            response, [], [], Confidence.High, meta);

        _queryEngine.GetContextAsync(
                Arg.Any<RoutingContext>(), resolvedId,
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Success(envelope));
    }

    [Fact]
    public async Task Handler_NotFound_SuggestsSearch()
    {
        _queryEngine.GetContextAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("Symbol", MethodId)));

        var result = await _handler.HandleGetContextAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = MethodId },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("symbols.search");
        result.Content.Should().Contain("DoAsync");
    }

    private static ResponseEnvelope<SymbolCard> MakeCardEnvelope()
    {
        var symbolId = SymbolId.From("M:MyNs.MyService.DoAsync");
        var card = SymbolCard.CreateMinimal(
            symbolId, "MyNs.MyService.DoAsync", SymbolKind.Method,
            "public async Task DoAsync()", "MyNs",
            FilePath.From("src/MyService.cs"), 10, 20, "public", Confidence.High);
        var meta = new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
            new Dictionary<string, LimitApplied>(), 0, 0m);
        return new ResponseEnvelope<SymbolCard>("answer", card, [], [], Confidence.High, meta);
    }
}
