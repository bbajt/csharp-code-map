namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class IndexHandlerTests : IDisposable
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Use a real temp file so File.Exists check passes
    private readonly string _tempSolutionPath;

    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IRoslynCompiler _compiler = Substitute.For<IRoslynCompiler>();
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly IBaselineCacheManager _cache = Substitute.For<IBaselineCacheManager>();
    private readonly IndexHandler _handler;

    private readonly CompilationResult _fakeCompilation = new(
        Symbols: [],
        References: [],
        Files: [],
        Stats: new IndexStats(SymbolCount: 10, ReferenceCount: 5, FileCount: 3, ElapsedSeconds: 1.0, Confidence: Confidence.High));

    public IndexHandlerTests()
    {
        _tempSolutionPath = Path.Combine(Path.GetTempPath(), $"CodeMapTest_{Guid.NewGuid():N}.sln");
        File.WriteAllText(_tempSolutionPath, ""); // create empty .sln file

        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _compiler.CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_fakeCompilation);
        _store.CreateBaselineAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CompilationResult>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        // Default: cache miss (no pull)
        _cache.PullAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        _handler = new IndexHandler(_git, _compiler, _store, _cache, new RepoRegistry(), NullLogger<IndexHandler>.Instance);
    }

    public void Dispose() => File.Delete(_tempSolutionPath);

    [Fact]
    public async Task EnsureBaseline_NewIndex_CompilesAndStores()
    {
        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _compiler.Received(1).CompileAndExtractAsync(_tempSolutionPath, Arg.Any<CancellationToken>());
        await _store.Received(1).CreateBaselineAsync(
            Arg.Any<RepoId>(),
            Arg.Any<CommitSha>(),
            Arg.Any<CompilationResult>(),
            RepoPath,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBaseline_NewIndex_ReturnsStats()
    {
        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("already_existed").GetBoolean().Should().BeFalse();
        json.GetProperty("stats").GetProperty("symbol_count").GetInt32().Should().Be(10);
        json.GetProperty("stats").GetProperty("reference_count").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task EnsureBaseline_ExistingIndex_SkipsCompilation()
    {
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _compiler.DidNotReceive().CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBaseline_ExistingIndex_AlreadyExistedTrue()
    {
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        JsonDocument.Parse(result.Content).RootElement
            .GetProperty("already_existed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EnsureBaseline_MissingRepoPath_ReturnsError()
    {
        var result = await _handler.HandleAsync(
            new JsonObject { ["solution_path"] = _tempSolutionPath },
            CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureBaseline_MissingSolutionPath_ReturnsError()
    {
        var result = await _handler.HandleAsync(
            new JsonObject { ["repo_path"] = RepoPath },
            CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureBaseline_InvalidSolutionPath_ReturnsError()
    {
        var result = await _handler.HandleAsync(
            Args(RepoPath, "/does/not/exist.sln"),
            CancellationToken.None);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureBaseline_PassesRepoRootPath_ToBaselineStore()
    {
        string? capturedRepoRootPath = null;
        _store.CreateBaselineAsync(
                Arg.Any<RepoId>(),
                Arg.Any<CommitSha>(),
                Arg.Any<CompilationResult>(),
                Arg.Do<string>(s => capturedRepoRootPath = s),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        capturedRepoRootPath.Should().Be(RepoPath);
    }

    [Fact]
    public void EnsureBaseline_RegistersToolInRegistry()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);
        registry.Find("index.ensure_baseline").Should().NotBeNull();
    }

    // ── Cache hit tests (PHASE-03-08) ─────────────────────────────────────────

    [Fact]
    public async Task EnsureBaseline_CacheHit_SkipsCompilation()
    {
        // Cache returns a local path — simulates successful pull
        _cache.PullAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/fake/local/path.db"));
        // After pull, store reports baseline exists
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(false, true); // first call returns false, second (post-pull) returns true

        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _compiler.DidNotReceive().CompileAndExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBaseline_CacheHit_FromCacheTrue()
    {
        _cache.PullAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/fake/local/path.db"));
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("already_existed").GetBoolean().Should().BeTrue();
        json.GetProperty("from_cache").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EnsureBaseline_CacheMiss_PushesAfterBuild()
    {
        // No local, no cache → build → push
        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _cache.Received(1).PushAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBaseline_CacheMiss_FromCacheFalse()
    {
        var result = await _handler.HandleAsync(Args(RepoPath, _tempSolutionPath), CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonDocument.Parse(result.Content).RootElement;
        json.GetProperty("from_cache").GetBoolean().Should().BeFalse();
    }

    // ── Solution auto-discovery tests ──────────────────────────────────────────

    [Fact]
    public async Task EnsureBaseline_NoSolutionPath_AutoDiscoversSlnInRepoRoot()
    {
        // Create a temp directory with a .sln file
        var tempDir = Path.Combine(Path.GetTempPath(), $"codemap-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Test.sln"), "");
            _git.GetRepoIdentityAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns(RepoId.From("disc-repo"));
            _git.GetCurrentCommitAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns(CommitSha.From(ValidSha));

            var result = await _handler.HandleAsync(
                new JsonObject { ["repo_path"] = tempDir },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task EnsureBaseline_NoSolutionPath_PrefersSlnx()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codemap-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Test.sln"), "");
            File.WriteAllText(Path.Combine(tempDir, "Test.slnx"), "");
            _git.GetRepoIdentityAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns(RepoId.From("disc-repo"));
            _git.GetCurrentCommitAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns(CommitSha.From(ValidSha));

            var result = await _handler.HandleAsync(
                new JsonObject { ["repo_path"] = tempDir },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task EnsureBaseline_WrongExtension_AutoCorrectsSlnToSlnx()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codemap-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Only .slnx exists, but agent passes .sln
            File.WriteAllText(Path.Combine(tempDir, "Test.slnx"), "");
            _git.GetRepoIdentityAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns(RepoId.From("disc-repo"));
            _git.GetCurrentCommitAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns(CommitSha.From(ValidSha));

            var result = await _handler.HandleAsync(
                new JsonObject
                {
                    ["repo_path"] = tempDir,
                    ["solution_path"] = Path.Combine(tempDir, "Test.sln") // wrong extension
                },
                CancellationToken.None);

            result.IsError.Should().BeFalse();
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task EnsureBaseline_NoSolutionFile_ReturnsClearError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codemap-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = await _handler.HandleAsync(
                new JsonObject { ["repo_path"] = tempDir },
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("No .sln or .slnx file found");
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // ── Short SHA resolution tests ───────────────────────────────────────────

    [Fact]
    public async Task EnsureBaseline_ShortSha_ResolvesViaGitService()
    {
        var fullSha = CommitSha.From(new string('c', 40));
        _git.ResolveCommitAsync(RepoPath, "abc1234", Arg.Any<CancellationToken>())
            .Returns(fullSha);

        var result = await _handler.HandleAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["solution_path"] = _tempSolutionPath,
                ["commit_sha"] = "abc1234"
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _git.Received(1).ResolveCommitAsync(RepoPath, "abc1234", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBaseline_InvalidCommitish_ReturnsHelpfulError()
    {
        _git.ResolveCommitAsync(RepoPath, "nonexistent", Arg.Any<CancellationToken>())
            .Returns((CommitSha?)null);

        var result = await _handler.HandleAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["solution_path"] = _tempSolutionPath,
                ["commit_sha"] = "nonexistent"
            },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Could not resolve commit");
    }

    [Fact]
    public async Task EnsureBaseline_FullSha_StillWorks()
    {
        var result = await _handler.HandleAsync(
            new JsonObject
            {
                ["repo_path"] = RepoPath,
                ["solution_path"] = _tempSolutionPath,
                ["commit_sha"] = ValidSha
            },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
    }

    private static JsonObject Args(string repoPath, string solutionPath) =>
        new() { ["repo_path"] = repoPath, ["solution_path"] = solutionPath };
}
