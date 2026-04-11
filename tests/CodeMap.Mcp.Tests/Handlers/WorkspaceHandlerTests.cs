namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class WorkspaceHandlerTests : IDisposable
{
    private const string WorkspaceId = "ws-001";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Use a real temp directory so File.Exists/Directory.GetFiles work
    private readonly string _tempDir;
    private readonly string _tempSlnPath;
    private readonly string RepoPath;
    private readonly string SlnPath;

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

    public WorkspaceHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-ws-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempSlnPath = Path.Combine(_tempDir, "Test.sln");
        File.WriteAllText(_tempSlnPath, "");
        RepoPath = _tempDir;
        SlnPath = _tempSlnPath;

        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("test-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _manager.CreateWorkspaceAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(), Arg.Any<CommitSha>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Result<CreateWorkspaceResponse, CodeMapError>.Success(
                     new CreateWorkspaceResponse(
                         Core.Types.WorkspaceId.From(WorkspaceId),
                         CommitSha.From(ValidSha),
                         0)));

        _manager.ResetWorkspaceAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(), Arg.Any<CancellationToken>())
                 .Returns(Result<ResetWorkspaceResponse, CodeMapError>.Success(
                     new ResetWorkspaceResponse(
                         Core.Types.WorkspaceId.From(WorkspaceId),
                         PreviousRevision: 2,
                         NewRevision: 0)));

        _handler = new WorkspaceHandler(_manager, _git, NullLogger<WorkspaceHandler>.Instance);
    }

    // ── workspace.create ──────────────────────────────────────────────────────

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Create_ValidParams_DelegatesToWorkspaceManager()
    {
        var result = await _handler.HandleCreateAsync(Args(RepoPath, WorkspaceId, SlnPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _manager.Received(1).CreateWorkspaceAsync(
            Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(), Arg.Any<CommitSha>(),
            Arg.Any<string>(), RepoPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_MissingRepoPath_ReturnsInvalidArgument()
    {
        var result = await _handler.HandleCreateAsync(Args(null, WorkspaceId, SlnPath), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("repo_path");
    }

    [Fact]
    public async Task Create_MissingWorkspaceId_ReturnsInvalidArgument()
    {
        var result = await _handler.HandleCreateAsync(Args(RepoPath, null, SlnPath), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("workspace_id");
    }

    [Fact]
    public async Task Create_MissingSolutionPath_AutoDiscovers()
    {
        // solution_path omitted — should auto-discover the .sln in temp dir
        var result = await _handler.HandleCreateAsync(Args(RepoPath, WorkspaceId, null), CancellationToken.None);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Create_DefaultCommitSha_UsesHead()
    {
        await _handler.HandleCreateAsync(Args(RepoPath, WorkspaceId, SlnPath), CancellationToken.None);

        await _git.Received(1).GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_ExplicitCommitSha_PassedThrough()
    {
        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["workspace_id"] = WorkspaceId,
            ["solution_path"] = SlnPath,
            ["commit_sha"] = ValidSha,
        };

        await _handler.HandleCreateAsync(args, CancellationToken.None);

        await _git.DidNotReceive().GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_ManagerReturnsError_ReturnsMcpError()
    {
        _manager.CreateWorkspaceAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(), Arg.Any<CommitSha>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Result<CreateWorkspaceResponse, CodeMapError>.Failure(
                     new CodeMapError(ErrorCodes.IndexNotAvailable, "No baseline")));

        var result = await _handler.HandleCreateAsync(Args(RepoPath, WorkspaceId, SlnPath), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── workspace.reset ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_ValidParams_DelegatesToWorkspaceManager()
    {
        var result = await _handler.HandleResetAsync(ResetArgs(RepoPath, WorkspaceId), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _manager.Received(1).ResetWorkspaceAsync(
            Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_MissingWorkspaceId_ReturnsInvalidArgument()
    {
        var result = await _handler.HandleResetAsync(ResetArgs(RepoPath, null), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("workspace_id");
    }

    [Fact]
    public async Task Reset_ManagerReturnsNotFound_ReturnsMcpError()
    {
        _manager.ResetWorkspaceAsync(
                    Arg.Any<RepoId>(), Arg.Any<Core.Types.WorkspaceId>(), Arg.Any<CancellationToken>())
                 .Returns(Result<ResetWorkspaceResponse, CodeMapError>.Failure(
                     CodeMapError.NotFound("Workspace", WorkspaceId)));

        var result = await _handler.HandleResetAsync(ResetArgs(RepoPath, WorkspaceId), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonObject Args(string? repoPath, string? workspaceId, string? solutionPath)
    {
        var obj = new JsonObject();
        if (repoPath is not null) obj["repo_path"] = repoPath;
        if (workspaceId is not null) obj["workspace_id"] = workspaceId;
        if (solutionPath is not null) obj["solution_path"] = solutionPath;
        return obj;
    }

    private static JsonObject ResetArgs(string? repoPath, string? workspaceId)
    {
        var obj = new JsonObject();
        if (repoPath is not null) obj["repo_path"] = repoPath;
        if (workspaceId is not null) obj["workspace_id"] = workspaceId;
        return obj;
    }
}
