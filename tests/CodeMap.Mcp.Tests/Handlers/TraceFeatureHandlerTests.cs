namespace CodeMap.Mcp.Tests.Handlers;

using System.Collections.Generic;
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

public sealed class TraceFeatureHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string EntryFqn = "M:OrderService.SubmitAsync";

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("trace-handler-repo");
    private static readonly SymbolId Entry = SymbolId.From(EntryFqn);

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly GraphHandler _handler;

    public TraceFeatureHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.TraceFeatureAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Success(MakeTraceEnvelope(Entry))));

        _handler = new GraphHandler(_engine, _git, NullLogger<GraphHandler>.Instance);
    }

    [Fact]
    public async Task TraceFeature_ValidParams_DelegatesToQueryEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["entry_point"] = EntryFqn,
        };

        var result = await _handler.HandleTraceFeatureAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _engine.Received(1).TraceFeatureAsync(
            Arg.Any<RoutingContext>(), Entry,
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TraceFeature_WithDepth_PassedToEngine()
    {
        int capturedDepth = -1;
        _engine.TraceFeatureAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedDepth = ci.ArgAt<int>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Success(MakeTraceEnvelope(Entry)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["entry_point"] = EntryFqn,
            ["depth"] = 5,
        };

        await _handler.HandleTraceFeatureAsync(args, CancellationToken.None);

        capturedDepth.Should().Be(5);
    }

    [Fact]
    public async Task TraceFeature_MissingRepoPath_ReturnsError()
    {
        var args = new JsonObject { ["entry_point"] = EntryFqn };

        var result = await _handler.HandleTraceFeatureAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task TraceFeature_MissingEntryPoint_ReturnsError()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleTraceFeatureAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task TraceFeature_WithWorkspaceId_UsesWorkspaceRouting()
    {
        RoutingContext? capturedRouting = null;
        _engine.TraceFeatureAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedRouting = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(
                       Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>.Success(MakeTraceEnvelope(Entry)));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["entry_point"] = EntryFqn,
            ["workspace_id"] = "ws-trace-001",
        };

        await _handler.HandleTraceFeatureAsync(args, CancellationToken.None);

        capturedRouting.Should().NotBeNull();
        capturedRouting!.Consistency.Should().Be(ConsistencyMode.Workspace);
        capturedRouting.WorkspaceId!.Value.Value.Should().Be("ws-trace-001");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ResponseEnvelope<FeatureTraceResponse> MakeTraceEnvelope(SymbolId entry)
    {
        var rootNode = new TraceNode(entry, null, entry.Value, 0, [], []);
        var data = new FeatureTraceResponse(entry, entry.Value, null, [rootNode], 1, 3, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<FeatureTraceResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
