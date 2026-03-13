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

public sealed class SurfacesHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WsIdStr = "ws-surfaces-001";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly SurfacesHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("surfaces-test-repo");

    public SurfacesHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.ListEndpointsAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Success(
                       MakeEndpointsEnvelope([
                           MakeEndpoint("GET", "/api/orders"),
                       ]))));

        _handler = new SurfacesHandler(_engine, _git, NullLogger<SurfacesHandler>.Instance);
    }

    [Fact]
    public async Task ListEndpoints_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
        await _engine.Received(1).ListEndpointsAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListEndpoints_WithPathFilter_PassedToEngine()
    {
        string? capturedPath = null;
        _engine.ListEndpointsAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedPath = ci.ArgAt<string?>(1);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Success(
                           MakeEndpointsEnvelope([])));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["path_filter"] = "/api/orders",
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        capturedPath.Should().Be("/api/orders");
    }

    [Fact]
    public async Task ListEndpoints_WithHttpMethod_PassedToEngine()
    {
        string? capturedMethod = null;
        _engine.ListEndpointsAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedMethod = ci.ArgAt<string?>(2);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Success(
                           MakeEndpointsEnvelope([])));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["http_method"] = "POST",
        };
        await _handler.HandleAsync(args, CancellationToken.None);

        capturedMethod.Should().Be("POST");
    }

    [Fact]
    public async Task ListEndpoints_NoEndpoints_ReturnsEmptyList()
    {
        _engine.ListEndpointsAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>.Success(
                       MakeEndpointsEnvelope([]))));

        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("endpoints");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EndpointInfo MakeEndpoint(string method, string path) =>
        new(HttpMethod: method,
            RoutePath: path,
            HandlerSymbol: SymbolId.From($"M:Fake.Controller.{method}"),
            FilePath: FilePath.From("Fake/Controller.cs"),
            Line: 1,
            Confidence: Confidence.High);

    private static ResponseEnvelope<ListEndpointsResponse> MakeEndpointsEnvelope(
        IReadOnlyList<EndpointInfo> endpoints)
    {
        var data = new ListEndpointsResponse(endpoints, endpoints.Count, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<ListEndpointsResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
