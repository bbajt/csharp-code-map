namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class SummaryHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "cccccccccccccccccccccccccccccccccccccccc";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly SummaryHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("summary-test-repo");

    public SummaryHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.SummarizeAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<string[]?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>.Success(
                       MakeSummarizeEnvelope("TestSolution"))));

        _handler = new SummaryHandler(_engine, _git, new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<SummaryHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
        await _engine.Received(1).SummarizeAsync(
            Arg.Any<RoutingContext>(),
            Arg.Is<string?>(s => s == RepoPath),
            Arg.Any<string[]?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingRepoPath_ReturnsInvalidArgError()
    {
        var args = new JsonObject();

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("repo_path");
        await _engine.DidNotReceive().SummarizeAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<string?>(),
            Arg.Any<string[]?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NullArgs_ReturnsInvalidArgError()
    {
        var result = await _handler.HandleAsync(null, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithSectionFilter_PassedToEngine()
    {
        string[]? capturedFilter = null;
        _engine.SummarizeAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<string[]?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedFilter = ci.ArgAt<string[]?>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>.Success(
                           MakeSummarizeEnvelope("TestSolution")));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["section_filter"] = new JsonArray { JsonValue.Create("api"), JsonValue.Create("overview") },
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        capturedFilter.Should().NotBeNull();
        capturedFilter.Should().Contain("api");
        capturedFilter.Should().Contain("overview");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly CommitSha TestSha = CommitSha.From(new string('c', 40));

    private static ResponseEnvelope<SummarizeResponse> MakeSummarizeEnvelope(string solutionName)
    {
        var stats = new SummaryStats(
            ProjectCount: 1, SymbolCount: 100, ReferenceCount: 200, FactCount: 5,
            EndpointCount: 2, ConfigKeyCount: 1, DbTableCount: 1, DiRegistrationCount: 1,
            ExceptionTypeCount: 0, LogTemplateCount: 0,
            SemanticLevel: SemanticLevel.Full);

        var response = new SummarizeResponse(
            SolutionName: solutionName,
            Markdown: $"# {solutionName} — Codebase Summary\n",
            Sections: [new SummarySection("Solution Overview", "content", 1)],
            Stats: stats);

        var timing = new TimingBreakdown(0, 0, 0, 0);
        var meta = new ResponseMeta(
            Timing: timing,
            BaselineCommitSha: TestSha,
            LimitsApplied: new Dictionary<string, LimitApplied>(),
            TokensSaved: 500,
            CostAvoided: 0.001m);

        return new ResponseEnvelope<SummarizeResponse>(
            Answer: $"Summary of '{solutionName}'.",
            Data: response,
            Evidence: [],
            NextActions: [],
            Confidence: Confidence.High,
            Meta: meta);
    }
}
