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

public sealed class ConfigKeysHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WsIdStr = "ws-config-001";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly SurfacesHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("config-test-repo");

    public ConfigKeysHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.ListConfigKeysAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Success(
                       MakeConfigKeysEnvelope([
                           MakeConfigKey("ConnectionStrings:DefaultDB", "IConfiguration indexer"),
                       ]))));

        _handler = new SurfacesHandler(_engine, _git, new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<SurfacesHandler>.Instance);
    }

    [Fact]
    public async Task ListConfigKeys_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleConfigKeysAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
        await _engine.Received(1).ListConfigKeysAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListConfigKeys_WithKeyFilter_PassedToEngine()
    {
        string? capturedFilter = null;
        _engine.ListConfigKeysAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedFilter = ci.ArgAt<string?>(1);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Success(
                           MakeConfigKeysEnvelope([])));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["key_filter"] = "App:",
        };
        await _handler.HandleConfigKeysAsync(args, CancellationToken.None);

        capturedFilter.Should().Be("App:");
    }

    [Fact]
    public async Task ListConfigKeys_NoKeys_ReturnsEmptyList()
    {
        _engine.ListConfigKeysAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Success(
                       MakeConfigKeysEnvelope([]))));

        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleConfigKeysAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("keys");
    }

    [Fact]
    public async Task ListConfigKeys_WithWorkspaceId_SetsWorkspaceMode()
    {
        RoutingContext? capturedRouting = null;
        _engine.ListConfigKeysAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedRouting = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>.Success(
                           MakeConfigKeysEnvelope([])));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleConfigKeysAsync(args, CancellationToken.None);

        capturedRouting.Should().NotBeNull();
        capturedRouting!.Consistency.Should().Be(ConsistencyMode.Workspace);
        capturedRouting.WorkspaceId.Should().NotBeNull();
        capturedRouting.WorkspaceId!.Value.Value.Should().Be(WsIdStr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConfigKeyInfo MakeConfigKey(string key, string pattern) =>
        new(Key: key,
            UsedBySymbol: SymbolId.From($"M:Fake.Service.Get"),
            FilePath: FilePath.From("Fake/Service.cs"),
            Line: 1,
            UsagePattern: pattern,
            Confidence: Confidence.High);

    private static ResponseEnvelope<ListConfigKeysResponse> MakeConfigKeysEnvelope(
        IReadOnlyList<ConfigKeyInfo> keys)
    {
        var data = new ListConfigKeysResponse(keys, keys.Count, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<ListConfigKeysResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
