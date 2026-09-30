namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class RemoveRepoHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private static readonly string ValidSha = new string('a', 40);
    private static readonly RepoId TestRepoId = RepoId.From("test-repo");

    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IRoslynCompiler _compiler = Substitute.For<IRoslynCompiler>();
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly IBaselineCacheManager _cache = Substitute.For<IBaselineCacheManager>();
    private readonly IBaselineScanner _scanner = Substitute.For<IBaselineScanner>();
    private readonly IndexHandler _handler;

    public RemoveRepoHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(TestRepoId);
        _scanner.RemoveRepoAsync(Arg.Any<RepoId>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 0, 0, [], DryRun: true));

        _handler = new IndexHandler(
            _git, _compiler, _store, _cache, new RepoRegistry(),
            NullLogger<IndexHandler>.Instance,
            scanner: _scanner);
    }

    [Fact]
    public void Register_RegistersRemoveRepoTool()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        registry.Find("index_remove_repo").Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveRepo_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleRemoveRepoAsync(new JsonObject(), CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveRepo_DryRunByDefault_AppendsDryRunNote()
    {
        _scanner.RemoveRepoAsync(TestRepoId, true, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 2, 1024, [], DryRun: true));

        var args = new JsonObject { ["repo_path"] = RepoPath };
        var result = await _handler.HandleRemoveRepoAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Dry run");
    }

    [Fact]
    public async Task RemoveRepo_DryRunFalse_NoNote()
    {
        _scanner.RemoveRepoAsync(TestRepoId, false, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 2, 1024, [], DryRun: false));

        var args = new JsonObject { ["repo_path"] = RepoPath, ["dry_run"] = false };
        var result = await _handler.HandleRemoveRepoAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotContain("Dry run");
    }

    /// <summary>PHASE-21-13 T02: a stringified boolean is honoured, not an INTERNAL_ERROR.</summary>
    [Fact]
    public async Task RemoveRepo_DryRunStringFalse_NoNote()
    {
        _scanner.RemoveRepoAsync(TestRepoId, false, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 2, 1024, [], DryRun: false));

        var args = new JsonObject { ["repo_path"] = RepoPath, ["dry_run"] = "false" };
        var result = await _handler.HandleRemoveRepoAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotContain("Dry run");
    }
    // ── PHASE-21-10 T01 ──────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveRepo_NoRepoPath_InvalidArgument_EvenWithOneRegisteredRepo()
    {
        // Before: the v2.4.0 auto-default resolved the only registered repo and deleted it.
        var registry = new RepoRegistry();
        registry.Register(RepoPath);
        var handler = new IndexHandler(_git, _compiler, _store, _cache, registry,
            NullLogger<IndexHandler>.Instance, scanner: _scanner);

        var result = await handler.HandleRemoveRepoAsync(new JsonObject { ["dry_run"] = false }, CancellationToken.None);

        result.IsError.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("code").GetString().Should().Be("INVALID_ARGUMENT");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("explicit repo_path").And.Contain(RepoPath);
        await _scanner.DidNotReceive().RemoveRepoAsync(Arg.Any<RepoId>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Schema_RepoPathRequired()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);

        registry.Find("index_remove_repo")!.InputSchema["required"]!.AsArray()
            .Select(n => n!.GetValue<string>()).Should().Contain("repo_path");
    }

    [Fact]
    public async Task RemoveRepo_WorkspacesInUse_ReturnsWorkspaceInUse_AndKeepsRepoRegistered()
    {
        _scanner.RemoveRepoAsync(TestRepoId, false, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 0, 0, [], DryRun: false, WorkspacesInUse: ["ws-other"]));
        var registry = new RepoRegistry();
        registry.Register(RepoPath);
        var handler = new IndexHandler(_git, _compiler, _store, _cache, registry,
            NullLogger<IndexHandler>.Instance, scanner: _scanner);

        var result = await handler.HandleRemoveRepoAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["dry_run"] = false }, CancellationToken.None);

        result.IsError.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("code").GetString().Should().Be("WORKSPACE_IN_USE");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("ws-other");
        registry.KnownRepos.Should().ContainSingle("nothing was deleted, so the repo stays registered");
    }

    [Fact]
    public async Task RemoveRepo_DryRunWithWorkspacesInUse_ReportsWithoutError()
    {
        _scanner.RemoveRepoAsync(TestRepoId, true, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 1, 10, [CommitSha.From(ValidSha)], DryRun: true,
                WorkspacesInUse: ["ws-other"]));

        var result = await _handler.HandleRemoveRepoAsync(new JsonObject { ["repo_path"] = RepoPath }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("workspaces_in_use").And.Contain("ws-other");
    }

    [Fact]
    public async Task RemoveRepo_SkippedInUse_RepoNotForgotten()
    {
        _scanner.RemoveRepoAsync(TestRepoId, false, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 0, 0, [], DryRun: false, SkippedInUse: [CommitSha.From(ValidSha)]));
        var registry = new RepoRegistry();
        registry.Register(RepoPath);
        var handler = new IndexHandler(_git, _compiler, _store, _cache, registry,
            NullLogger<IndexHandler>.Instance, scanner: _scanner);

        var result = await handler.HandleRemoveRepoAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["dry_run"] = false }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        registry.KnownRepos.Should().ContainSingle("a baseline is still there, so the repo stays registered");
    }

    [Fact]
    public async Task RemoveRepo_OwnWorkspace_DeletedThroughWorkspaceManager_AndStickyCleared()
    {
        var manager = Substitute.For<WorkspaceManager>(
            Substitute.For<IOverlayStore>(), Substitute.For<IIncrementalCompiler>(), Substitute.For<ISymbolStore>(),
            Substitute.For<IGitService>(), Substitute.For<ICacheService>(), Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);
        manager.ListWorkspacesAsync(TestRepoId, Arg.Any<CancellationToken>())
            .Returns([new CodeMap.Query.WorkspaceSummary(WorkspaceId.From("ws-mine"), CommitSha.From(ValidSha), 1, 0)]);
        _scanner.RemoveRepoAsync(TestRepoId, false, Arg.Any<CancellationToken>())
            .Returns(new RemoveRepoResponse(TestRepoId, 1, 10, [CommitSha.From(ValidSha)], DryRun: false,
                WorkspacesRemoved: ["ws-mine"]));
        var sticky = new WorkspaceStickyRegistry();
        sticky.Set(RepoPath, "ws-mine");
        var handler = new IndexHandler(_git, _compiler, _store, _cache, new RepoRegistry(),
            NullLogger<IndexHandler>.Instance, scanner: _scanner, workspaceManager: manager, stickyRegistry: sticky);

        var result = await handler.HandleRemoveRepoAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["dry_run"] = false }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await manager.Received(1).DeleteWorkspaceAsync(TestRepoId, WorkspaceId.From("ws-mine"), Arg.Any<CancellationToken>());
        sticky.Get(RepoPath).Should().BeNull();
    }
}
