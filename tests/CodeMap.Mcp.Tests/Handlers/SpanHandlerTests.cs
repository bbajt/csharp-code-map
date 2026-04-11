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

public sealed class SpanHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FilePath = "src/MyClass.cs";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    private static ResponseEnvelope<SpanResponse> FakeSpanEnvelope(string sha) =>
        new(
            "Got span.", new SpanResponse(
                FilePath: Core.Types.FilePath.From("src/MyClass.cs"),
                StartLine: 1,
                EndLine: 20,
                TotalFileLines: 50,
                Content: "// code here\n",
                Truncated: false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(sha),
                new Dictionary<string, LimitApplied>(), 0, 0m));

    public SpanHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _queryEngine.GetSpanAsync(
                Arg.Any<RoutingContext>(), Arg.Any<Core.Types.FilePath>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(FakeSpanEnvelope(ValidSha)));

        _queryEngine.GetDefinitionSpanAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(FakeSpanEnvelope(ValidSha)));

        _handler = new McpToolHandlers(_queryEngine, _git, NullLogger<McpToolHandlers>.Instance);
    }

    // ── code.get_span ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSpan_ValidRange_DelegatesToQueryEngine()
    {
        var result = await _handler.HandleGetSpanAsync(
            SpanArgs(RepoPath, FilePath, 1, 20),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetSpanAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<Core.Types.FilePath>(),
            1, 20, Arg.Any<int>(),
            Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSpan_WithContextLines_PassesCorrectly()
    {
        int capturedContext = -1;
        _queryEngine.GetSpanAsync(
                Arg.Any<RoutingContext>(), Arg.Any<Core.Types.FilePath>(),
                Arg.Any<int>(), Arg.Any<int>(),
                Arg.Do<int>(c => capturedContext = c),
                Arg.Any<BudgetLimits?>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(FakeSpanEnvelope(ValidSha)));

        await _handler.HandleGetSpanAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["file_path"] = FilePath,
                ["start_line"] = 10,
                ["end_line"] = 15,
                ["context_lines"] = 3,
            },
            CancellationToken.None);

        capturedContext.Should().Be(3);
    }

    [Fact]
    public async Task GetSpan_MissingFilePath_ReturnsError()
    {
        var result = await _handler.HandleGetSpanAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["start_line"] = 1, ["end_line"] = 10 },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void GetSpan_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.RegisterQueryTools(registry);
        registry.Find("code.get_span").Should().NotBeNull();
    }

    // ── symbols.get_definition_span ───────────────────────────────────────────

    [Fact]
    public async Task GetDefinitionSpan_ValidSymbol_DelegatesToQueryEngine()
    {
        var result = await _handler.HandleGetDefinitionSpanAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = "MyNs.MyClass" },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _queryEngine.Received(1).GetDefinitionSpanAsync(
            Arg.Any<RoutingContext>(),
            SymbolId.From("MyNs.MyClass"),
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDefinitionSpan_MissingSymbolId_ReturnsError()
    {
        var result = await _handler.HandleGetDefinitionSpanAsync(
            new JsonObject { ["repo_path"] = RepoPath },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void GetDefinitionSpan_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.RegisterQueryTools(registry);
        registry.Find("symbols.get_definition_span").Should().NotBeNull();
    }

    [Fact]
    public async Task GetSpan_UnknownFilePath_ReturnsDescriptiveError()
    {
        var result = await _handler.HandleGetSpanAsync(
            SpanArgs(RepoPath, "unknown", 1, 20),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("no source location");
        result.Content.Should().Contain("symbols.get_card");
    }

    private static JsonObject SpanArgs(string repo, string file, int start, int end) =>
        new()
        {
            ["repo_path"] = repo,
            ["file_path"] = file,
            ["start_line"] = start,
            ["end_line"] = end,
        };
}
