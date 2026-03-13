namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class RepoStatusHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ValidSha2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly WorkspaceManager _workspaceManager = Substitute.For<WorkspaceManager>(
        Substitute.For<IOverlayStore>(),
        Substitute.For<IIncrementalCompiler>(),
        Substitute.For<ISymbolStore>(),
        Substitute.For<IGitService>(),
        Substitute.For<ICacheService>(),
        Substitute.For<IResolutionWorker>(),
        NullLogger<WorkspaceManager>.Instance);
    private readonly RepoStatusHandler _handler;

    public RepoStatusHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));
        _git.GetCurrentBranchAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns("main");
        _git.IsCleanAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(true);
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _workspaceManager.ListWorkspacesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkspaceSummary>());

        _handler = new RepoStatusHandler(_git, _store, _workspaceManager, NullLogger<RepoStatusHandler>.Instance);
    }

    [Fact]
    public async Task RepoStatus_ValidRepo_ReturnsStatusJson()
    {
        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("repo_id").GetString().Should().Be("my-repo");
        json.GetProperty("current_commit_sha").GetString().Should().Be(ValidSha);
        json.GetProperty("branch_name").GetString().Should().Be("main");
        json.GetProperty("is_clean").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RepoStatus_WithBaseline_BaselineExistsTrue()
    {
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        JsonDocument.Parse(result.Content).RootElement
            .GetProperty("baseline_index_exists").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RepoStatus_WithoutBaseline_BaselineExistsFalse()
    {
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        JsonDocument.Parse(result.Content).RootElement
            .GetProperty("baseline_index_exists").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task RepoStatus_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleAsync(null, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RepoStatus_IncludesCurrentBranch()
    {
        _git.GetCurrentBranchAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns("feature/my-branch");

        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        JsonDocument.Parse(result.Content).RootElement
            .GetProperty("branch_name").GetString().Should().Be("feature/my-branch");
    }

    [Fact]
    public async Task RepoStatus_IncludesCleanStatus()
    {
        _git.IsCleanAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        JsonDocument.Parse(result.Content).RootElement
            .GetProperty("is_clean").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void RepoStatus_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        registry.Find("repo.status").Should().NotBeNull();
    }

    [Fact]
    public async Task RepoStatus_WithActiveWorkspaces_IncludesWorkspaceSummaries()
    {
        var sha = CommitSha.From(ValidSha);
        _workspaceManager.ListWorkspacesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceSummary[]
            {
                new(WorkspaceId.From("ws-001"), sha, OverlayRevision: 2, ModifiedFileCount: 3),
            });

        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var workspaces = JsonDocument.Parse(result.Content).RootElement.GetProperty("workspaces");
        workspaces.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RepoStatus_NoWorkspaces_ReturnsEmptyList()
    {
        var result = await _handler.HandleAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var workspaces = JsonDocument.Parse(result.Content).RootElement.GetProperty("workspaces");
        workspaces.GetArrayLength().Should().Be(0);
    }

    private static JsonObject Args(string repoPath) =>
        new() { ["repo_path"] = repoPath };
}
