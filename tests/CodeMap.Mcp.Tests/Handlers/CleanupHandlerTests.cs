namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class CleanupHandlerTests
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

    public CleanupHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(TestRepoId);
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));
        _scanner.ListBaselinesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BaselineInfo>>([]));
        _scanner.CleanupBaselinesAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<IReadOnlySet<CommitSha>>(),
            Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CleanupResponse(0, 0, [], [], DryRun: true));

        _handler = new IndexHandler(
            _git, _compiler, _store, _cache, new RepoRegistry(),
            NullLogger<IndexHandler>.Instance,
            scanner: _scanner);
    }

    [Fact]
    public void Register_RegistersCleanupTool()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        registry.Find("index.cleanup").Should().NotBeNull();
    }

    [Fact]
    public async Task Cleanup_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleCleanupAsync(new JsonObject(), CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_DryRunDefault_True()
    {
        bool capturedDryRun = false;
        _scanner.CleanupBaselinesAsync(
                Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<IReadOnlySet<CommitSha>>(),
                Arg.Any<int>(), Arg.Any<int?>(), Arg.Do<bool>(v => capturedDryRun = v), Arg.Any<CancellationToken>())
            .Returns(new CleanupResponse(0, 0, [], [], DryRun: true));

        await _handler.HandleCleanupAsync(Args(RepoPath), CancellationToken.None);

        capturedDryRun.Should().BeTrue("dry_run defaults to true");
    }

    [Fact]
    public async Task Cleanup_DryRunFalse_PassedToScanner()
    {
        bool capturedDryRun = true;
        _scanner.CleanupBaselinesAsync(
                Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<IReadOnlySet<CommitSha>>(),
                Arg.Any<int>(), Arg.Any<int?>(), Arg.Do<bool>(v => capturedDryRun = v), Arg.Any<CancellationToken>())
            .Returns(new CleanupResponse(0, 0, [], [], DryRun: false));

        await _handler.HandleCleanupAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["dry_run"] = false },
            CancellationToken.None);

        capturedDryRun.Should().BeFalse();
    }

    [Fact]
    public async Task Cleanup_KeepCountPassedToScanner()
    {
        int capturedKeepCount = -1;
        _scanner.CleanupBaselinesAsync(
                Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<IReadOnlySet<CommitSha>>(),
                Arg.Do<int>(v => capturedKeepCount = v), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CleanupResponse(0, 0, [], [], DryRun: true));

        await _handler.HandleCleanupAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["keep_count"] = 3 },
            CancellationToken.None);

        capturedKeepCount.Should().Be(3);
    }

    [Fact]
    public async Task Cleanup_ValidParams_ReturnsFormattedResponse()
    {
        var sha = CommitSha.From(ValidSha);
        _scanner.CleanupBaselinesAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<IReadOnlySet<CommitSha>>(),
            Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CleanupResponse(
                BaselinesRemoved: 2,
                BytesReclaimed: 1024,
                RemovedCommits: [sha],
                KeptCommits: [sha],
                DryRun: true));

        var result = await _handler.HandleCleanupAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        // Extract JSON portion (dry-run note is appended after the JSON object)
        var jsonPart = result.Content[..result.Content.IndexOf('\n', StringComparison.Ordinal)];
        var json = JsonDocument.Parse(jsonPart).RootElement;
        json.GetProperty("baselines_removed").GetInt32().Should().Be(2);
        json.GetProperty("bytes_reclaimed").GetInt64().Should().Be(1024);
        json.GetProperty("dry_run").GetBoolean().Should().BeTrue();
        // Dry-run note must be present
        result.Content.Should().Contain("Dry run", "dry_run:true must include a note clarifying no files were deleted");
    }

    [Fact]
    public async Task Cleanup_DelegatesToScanner()
    {
        await _handler.HandleCleanupAsync(Args(RepoPath), CancellationToken.None);

        await _scanner.Received(1).CleanupBaselinesAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<IReadOnlySet<CommitSha>>(),
            Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static JsonObject Args(string repoPath) =>
        new() { ["repo_path"] = repoPath };
}
