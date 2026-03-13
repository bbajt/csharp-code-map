namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class ListBaselinesHandlerTests
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

    public ListBaselinesHandlerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(TestRepoId);
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));
        _scanner.ListBaselinesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BaselineInfo>>([]));

        _handler = new IndexHandler(
            _git, _compiler, _store, _cache,
            NullLogger<IndexHandler>.Instance,
            scanner: _scanner);
    }

    [Fact]
    public void Register_RegistersListBaselinesTool()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        registry.Find("index.list_baselines").Should().NotBeNull();
    }

    [Fact]
    public async Task ListBaselines_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleListBaselinesAsync(new JsonObject(), CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ListBaselines_NullArgs_ReturnsError()
    {
        var result = await _handler.HandleListBaselinesAsync(null, CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ListBaselines_EmptyBaselines_ReturnsZeroCount()
    {
        var result = await _handler.HandleListBaselinesAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("baselines").GetArrayLength().Should().Be(0);
        json.GetProperty("total_size_bytes").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task ListBaselines_ValidParams_DelegatesToScanner()
    {
        await _handler.HandleListBaselinesAsync(Args(RepoPath), CancellationToken.None);

        await _scanner.Received(1).ListBaselinesAsync(
            Arg.Any<RepoId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListBaselines_ReturnsCurrentHead()
    {
        var result = await _handler.HandleListBaselinesAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("current_head").GetString().Should().Be(ValidSha);
    }

    [Fact]
    public async Task ListBaselines_CurrentHeadMarkedCorrectly()
    {
        var anotherSha = new string('b', 40);
        _scanner.ListBaselinesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BaselineInfo>>([
                new BaselineInfo(CommitSha.From(ValidSha), DateTimeOffset.UtcNow, 1024, false, false),
                new BaselineInfo(CommitSha.From(anotherSha), DateTimeOffset.UtcNow.AddHours(-1), 512, false, false),
            ]));

        var result = await _handler.HandleListBaselinesAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var baselines = JsonDocument.Parse(result.Content).RootElement.GetProperty("baselines");
        var first = baselines[0];
        first.GetProperty("is_current_head").GetBoolean().Should().BeTrue();
        baselines[1].GetProperty("is_current_head").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ListBaselines_TotalSizeSumsAllBaselines()
    {
        _scanner.ListBaselinesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BaselineInfo>>([
                new BaselineInfo(CommitSha.From(ValidSha), DateTimeOffset.UtcNow, 1000, false, false),
                new BaselineInfo(CommitSha.From(new string('b', 40)), DateTimeOffset.UtcNow.AddHours(-1), 2000, false, false),
            ]));

        var result = await _handler.HandleListBaselinesAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("total_size_bytes").GetInt64().Should().Be(3000);
    }

    [Fact]
    public async Task ListBaselines_FormattedResponse_ContainsExpectedFields()
    {
        var now = DateTimeOffset.UtcNow;
        _scanner.ListBaselinesAsync(Arg.Any<RepoId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BaselineInfo>>([
                new BaselineInfo(CommitSha.From(ValidSha), now, 4096, false, false),
            ]));

        var result = await _handler.HandleListBaselinesAsync(Args(RepoPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var baseline = JsonDocument.Parse(result.Content).RootElement
            .GetProperty("baselines")[0];

        baseline.TryGetProperty("commit_sha", out _).Should().BeTrue();
        baseline.TryGetProperty("created_at", out _).Should().BeTrue();
        baseline.TryGetProperty("size_bytes", out _).Should().BeTrue();
        baseline.TryGetProperty("is_current_head", out _).Should().BeTrue();
        baseline.TryGetProperty("is_active_workspace_base", out _).Should().BeTrue();
    }

    private static JsonObject Args(string repoPath) =>
        new() { ["repo_path"] = repoPath };
}
