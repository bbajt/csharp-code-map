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
        registry.Find("index.remove_repo").Should().NotBeNull();
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
}
