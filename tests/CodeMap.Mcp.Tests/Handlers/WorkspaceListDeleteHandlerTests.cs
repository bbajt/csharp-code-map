namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class WorkspaceListDeleteHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string WorkspaceId = "ws-001";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly Core.Types.WorkspaceId WsId = Core.Types.WorkspaceId.From(WorkspaceId);

    private readonly WorkspaceManager _manager = Substitute.For<WorkspaceManager>(
        Substitute.For<IOverlayStore>(),
        Substitute.For<IIncrementalCompiler>(),
        Substitute.For<ISymbolStore>(),
        Substitute.For<IGitService>(),
        Substitute.For<ICacheService>(),
        Substitute.For<IResolutionWorker>(),
        NullLogger<WorkspaceManager>.Instance);
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly WorkspaceHandler _handler;

    public WorkspaceListDeleteHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Repo);
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Sha);

        _handler = new WorkspaceHandler(_manager, _git, new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<WorkspaceHandler>.Instance);
    }

    // ── workspace.list ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListWorkspaces_ReturnsAllForRepo()
    {
        var summaries = new List<WorkspaceSummary>
        {
            new(WsId, Sha, 0, 0),
            new(Core.Types.WorkspaceId.From("ws-002"), Sha, 1, 3),
        };
        _manager.ListWorkspacesAsync(Repo, Arg.Any<CancellationToken>())
                .Returns(summaries);

        var result = await _handler.HandleListAsync(Args("repo_path", RepoPath), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("workspaces").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ListWorkspaces_EmptyWhenNoWorkspaces()
    {
        _manager.ListWorkspacesAsync(Repo, Arg.Any<CancellationToken>())
                .Returns(new List<WorkspaceSummary>());

        var result = await _handler.HandleListAsync(Args("repo_path", RepoPath), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("workspaces").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListWorkspaces_IncludesCurrentCommitSha()
    {
        _manager.ListWorkspacesAsync(Repo, Arg.Any<CancellationToken>())
                .Returns(new List<WorkspaceSummary>());

        var result = await _handler.HandleListAsync(Args("repo_path", RepoPath), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("current_commit_sha").GetString()
           .Should().Be(ValidSha);
    }

    [Fact]
    public async Task ListWorkspaces_StaleWorkspace_IsStaleTrue()
    {
        var stale = new WorkspaceSummary(WsId, CommitSha.From(OtherSha), 2, 1, IsStale: true);
        _manager.ListWorkspacesAsync(Repo, Arg.Any<CancellationToken>())
                .Returns(new List<WorkspaceSummary> { stale });

        var result = await _handler.HandleListAsync(Args("repo_path", RepoPath), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        var ws = doc.RootElement.GetProperty("workspaces")[0];
        ws.GetProperty("is_stale").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ListWorkspaces_FreshWorkspace_IsStaleFalse()
    {
        var fresh = new WorkspaceSummary(WsId, Sha, 0, 0, IsStale: false);
        _manager.ListWorkspacesAsync(Repo, Arg.Any<CancellationToken>())
                .Returns(new List<WorkspaceSummary> { fresh });

        var result = await _handler.HandleListAsync(Args("repo_path", RepoPath), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        var ws = doc.RootElement.GetProperty("workspaces")[0];
        ws.GetProperty("is_stale").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ListWorkspaces_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleListAsync(new JsonObject(), default);

        result.IsError.Should().BeTrue();
    }

    // ── workspace.delete ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteWorkspace_ExistingWorkspace_ReturnsDeletedTrue()
    {
        var info = new WorkspaceInfo(WsId, Repo, Sha, 0, "/sln", "/repo", DateTimeOffset.UtcNow);
        _manager.GetWorkspaceInfo(Repo, WsId).Returns(info);
        _manager.DeleteWorkspaceAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

        var result = await _handler.HandleDeleteAsync(
            Args("repo_path", RepoPath, "workspace_id", WorkspaceId), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DeleteWorkspace_NonExistent_ReturnsDeletedFalse()
    {
        _manager.GetWorkspaceInfo(Repo, WsId).Returns((WorkspaceInfo?)null);
        _manager.DeleteWorkspaceAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

        var result = await _handler.HandleDeleteAsync(
            Args("repo_path", RepoPath, "workspace_id", WorkspaceId), default);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeleteWorkspace_RemovesFromList()
    {
        var info = new WorkspaceInfo(WsId, Repo, Sha, 0, "/sln", "/repo", DateTimeOffset.UtcNow);
        _manager.GetWorkspaceInfo(Repo, WsId).Returns(info);
        _manager.DeleteWorkspaceAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

        await _handler.HandleDeleteAsync(
            Args("repo_path", RepoPath, "workspace_id", WorkspaceId), default);

        await _manager.Received(1).DeleteWorkspaceAsync(Repo, WsId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteWorkspace_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleDeleteAsync(
            Args("workspace_id", WorkspaceId), default);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteWorkspace_MissingWorkspaceId_ReturnsError()
    {
        var result = await _handler.HandleDeleteAsync(
            Args("repo_path", RepoPath), default);

        result.IsError.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonObject Args(params string[] kvPairs)
    {
        var obj = new JsonObject();
        for (int i = 0; i < kvPairs.Length - 1; i += 2)
            obj[kvPairs[i]] = kvPairs[i + 1];
        return obj;
    }
}
