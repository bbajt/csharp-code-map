namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class OverlayRefreshHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string WorkspaceId = "ws-001";

    private readonly WorkspaceManager _manager = Substitute.For<WorkspaceManager>(
        Substitute.For<IOverlayStore>(),
        Substitute.For<IIncrementalCompiler>(),
        Substitute.For<ISymbolStore>(),
        Substitute.For<IGitService>(),
        Substitute.For<ICacheService>(),
        Substitute.For<IResolutionWorker>(),
        NullLogger<WorkspaceManager>.Instance);
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly OverlayRefreshHandler _handler;

    public OverlayRefreshHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("test-repo"));

        _manager.RefreshOverlayAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
                    Arg.Any<IReadOnlyList<FilePath>?>(), Arg.Any<CancellationToken>())
                 .Returns(Result<RefreshOverlayResponse, CodeMapError>.Success(
                     new RefreshOverlayResponse(FilesReindexed: 1, SymbolsUpdated: 5, NewOverlayRevision: 2)));

        _handler = new OverlayRefreshHandler(_manager, _git, new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<OverlayRefreshHandler>.Instance);
    }

    [Fact]
    public async Task Refresh_ValidParams_DelegatesToWorkspaceManager()
    {
        var result = await _handler.HandleAsync(Args(RepoPath, WorkspaceId, null), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _manager.Received(1).RefreshOverlayAsync(
            Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
            Arg.Any<IReadOnlyList<FilePath>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithExplicitFiles_PassesFileList()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["workspace_id"] = WorkspaceId,
            ["file_paths"] = new JsonArray("src/Foo.cs", "src/Bar.cs"),
        };

        await _handler.HandleAsync(args, CancellationToken.None);

        await _manager.Received(1).RefreshOverlayAsync(
            Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
            Arg.Is<IReadOnlyList<FilePath>?>(fp => fp != null && fp.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithoutFiles_PassesNull()
    {
        await _handler.HandleAsync(Args(RepoPath, WorkspaceId, null), CancellationToken.None);

        await _manager.Received(1).RefreshOverlayAsync(
            Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
            Arg.Is<IReadOnlyList<FilePath>?>(fp => fp == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_MissingWorkspaceId_ReturnsInvalidArgument()
    {
        var result = await _handler.HandleAsync(Args(RepoPath, null, null), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("workspace_id");
    }

    [Fact]
    public async Task Refresh_ManagerReturnsError_ReturnsMcpError()
    {
        _manager.RefreshOverlayAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
                    Arg.Any<IReadOnlyList<FilePath>?>(), Arg.Any<CancellationToken>())
                 .Returns(Result<RefreshOverlayResponse, CodeMapError>.Failure(
                     CodeMapError.NotFound("Workspace", WorkspaceId)));

        var result = await _handler.HandleAsync(Args(RepoPath, WorkspaceId, null), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // ── Error-code taxonomy (PHASE-21-02 T02, ADR-041) ───────────────────────

    [Fact]
    public async Task Refresh_ManagerThrowsIo_ReturnsStorageErrorNotInvalidArgument()
    {
        _manager.RefreshOverlayAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
                    Arg.Any<IReadOnlyList<FilePath>?>(), Arg.Any<CancellationToken>())
                 .Returns<Result<RefreshOverlayResponse, CodeMapError>>(_ => throw new IOException("disk full"));

        var result = await _handler.HandleAsync(Args(RepoPath, WorkspaceId, null), CancellationToken.None);

        var json = JsonNode.Parse(result.Content)!;
        json["code"]!.GetValue<string>().Should().Be("STORAGE_ERROR");
        json["retryable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_WalLocked_ReturnsWorkspaceInUse()
    {
        var sharing = new IOException("The process cannot access the file 'overlay.wal' because it is being used by another process.",
            OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsMacOS() ? 35 : 11);
        _manager.RefreshOverlayAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(),
                    Arg.Any<IReadOnlyList<FilePath>?>(), Arg.Any<CancellationToken>())
                 .Returns<Result<RefreshOverlayResponse, CodeMapError>>(_ => throw sharing);

        var result = await _handler.HandleAsync(Args(RepoPath, WorkspaceId, null), CancellationToken.None);

        JsonNode.Parse(result.Content)!["code"]!.GetValue<string>().Should().Be("WORKSPACE_IN_USE");
    }

    private static JsonObject Args(string? repoPath, string? workspaceId, string[]? filePaths)
    {
        var obj = new JsonObject();
        if (repoPath is not null) obj["repo_path"] = repoPath;
        if (workspaceId is not null) obj["workspace_id"] = workspaceId;
        if (filePaths is not null)
        {
            var arr = new JsonArray();
            foreach (var p in filePaths) arr.Add(p);
            obj["file_paths"] = arr;
        }
        return obj;
    }
}
