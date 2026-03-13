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

public sealed class ExportHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ExportHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("export-test-repo");

    public ExportHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.ExportAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Success(
                       MakeExportEnvelope("standard", "markdown"))));

        _handler = new ExportHandler(_engine, _git, NullLogger<ExportHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
        await _engine.Received(1).ExportAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string[]?>(),
            Arg.Is<string?>(s => s == RepoPath),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingRepoPath_ReturnsInvalidArgError()
    {
        var args = new JsonObject();

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("repo_path");
        await _engine.DidNotReceive().ExportAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string[]?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NullArgs_ReturnsInvalidArgError()
    {
        var result = await _handler.HandleAsync(null, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithDetailAndFormat_PassedToEngine()
    {
        string? capturedDetail = null;
        string? capturedFormat = null;
        _engine.ExportAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedDetail = ci.ArgAt<string>(1);
                   capturedFormat = ci.ArgAt<string>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Success(
                           MakeExportEnvelope("full", "json")));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["detail"] = "full",
            ["format"] = "json",
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        capturedDetail.Should().Be("full");
        capturedFormat.Should().Be("json");
    }

    [Fact]
    public async Task HandleAsync_WithMaxTokens_PassedToEngine()
    {
        int capturedMaxTokens = 0;
        _engine.ExportAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedMaxTokens = ci.ArgAt<int>(3);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Success(
                           MakeExportEnvelope("standard", "markdown")));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["max_tokens"] = 8000,
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        capturedMaxTokens.Should().Be(8000);
    }

    [Fact]
    public async Task HandleAsync_WithSectionFilter_PassedToEngine()
    {
        string[]? capturedFilter = null;
        _engine.ExportAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedFilter = ci.ArgAt<string[]?>(4);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ExportResponse>, CodeMapError>.Success(
                           MakeExportEnvelope("standard", "markdown")));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["section_filter"] = new JsonArray { JsonValue.Create("public_api"), JsonValue.Create("interfaces") },
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        capturedFilter.Should().NotBeNull();
        capturedFilter.Should().Contain("public_api");
        capturedFilter.Should().Contain("interfaces");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly CommitSha TestSha = CommitSha.From(new string('e', 40));

    private static ResponseEnvelope<ExportResponse> MakeExportEnvelope(string detail, string format)
    {
        var stats = new SummaryStats(
            ProjectCount: 1, SymbolCount: 100, ReferenceCount: 200, FactCount: 5,
            EndpointCount: 2, ConfigKeyCount: 1, DbTableCount: 1, DiRegistrationCount: 1,
            ExceptionTypeCount: 0, LogTemplateCount: 0,
            SemanticLevel: SemanticLevel.Full);

        var response = new ExportResponse(
            Content: "# MySolution — Codebase Context\n",
            Format: format,
            DetailLevel: detail,
            EstimatedTokens: 100,
            Truncated: false,
            Stats: stats);

        var timing = new TimingBreakdown(0, 0, 0, 0);
        var meta = new ResponseMeta(
            Timing: timing,
            BaselineCommitSha: TestSha,
            LimitsApplied: new Dictionary<string, LimitApplied>(),
            TokensSaved: 500,
            CostAvoided: 0.001m);

        return new ResponseEnvelope<ExportResponse>(
            Answer: $"Exported codebase ({detail} detail).",
            Data: response,
            Evidence: [],
            NextActions: [],
            Confidence: Confidence.High,
            Meta: meta);
    }
}
