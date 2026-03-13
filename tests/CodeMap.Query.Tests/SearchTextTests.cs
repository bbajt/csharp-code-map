namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>Unit tests for QueryEngine.SearchTextAsync (PHASE-09-02).</summary>
public sealed class SearchTextTests
{
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(ValidSha);

    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly QueryEngine _engine;

    public SearchTextTests()
    {
        _store.BaselineExistsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _store.GetSemanticLevelAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns(SemanticLevel.Full);

        _engine = new QueryEngine(_store, _cache, _tracker,
            new ExcerptReader(_store), new GraphTraverser(),
            new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
    }

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    [Fact]
    public async Task SearchTextAsync_InvalidRegex_ReturnsInvalidArgument()
    {
        var routing = CommittedRouting();
        var result = await _engine.SearchTextAsync(routing, "[invalid", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
        result.Error.Message.Should().Contain("regex");
    }

    [Fact]
    public async Task SearchTextAsync_MissingRepoRoot_ReturnsIndexNotAvailable()
    {
        _store.GetAllFilePathsAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(new List<FilePath> { FilePath.From("src/Foo.cs") });
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _engine.SearchTextAsync(CommittedRouting(), "Foo", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.IndexNotAvailable);
    }

    [Fact]
    public async Task SearchTextAsync_PatternMatchesFile_ReturnsMatches()
    {
        // Arrange: create a real temp file
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Foo.cs");
        await File.WriteAllTextAsync(file,
            "using System;\nvar x = new OrderService();\nvar y = 42;");

        _store.GetAllFilePathsAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(new List<FilePath> { FilePath.From("src/Foo.cs") });
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(dir.Replace('\\', '/'));

        // Make absolute path resolve to our temp file (repoRoot/src/Foo.cs)
        var srcDir = Path.Combine(dir, "src");
        Directory.CreateDirectory(srcDir);
        File.Copy(file, Path.Combine(srcDir, "Foo.cs"), overwrite: true);

        try
        {
            var result = await _engine.SearchTextAsync(CommittedRouting(), "OrderService", null, null);

            result.IsSuccess.Should().BeTrue();
            result.Value.Data.Matches.Should().HaveCount(1);
            result.Value.Data.Matches[0].Line.Should().Be(2);
            result.Value.Data.Matches[0].Excerpt.Should().Contain("OrderService");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SearchTextAsync_FilePathFilter_OnlyMatchesFilteredFiles()
    {
        _store.GetAllFilePathsAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(new List<FilePath>
            {
                FilePath.From("src/Foo.cs"),
                FilePath.From("tests/FooTest.cs"),
            });
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns("C:/fake");

        // Filter to src/ — tests/FooTest.cs won't exist on disk so it's skipped anyway,
        // but the filter reduces the set before disk reads
        var result = await _engine.SearchTextAsync(CommittedRouting(), "anything", "src/", null);

        // File doesn't exist → 0 matches, but TotalFiles should be 1 (only src/ files)
        result.IsSuccess.Should().BeTrue();
        result.Value.Data.TotalFiles.Should().Be(1);
    }

    [Fact]
    public async Task SearchTextAsync_TruncationWorks_WhenMatchesExceedCap()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        // Create a file with 5 matching lines
        await File.WriteAllTextAsync(Path.Combine(dir, "Foo.cs"),
            string.Join('\n', Enumerable.Range(1, 5).Select(i => $"// match {i}")));

        _store.GetAllFilePathsAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(new List<FilePath> { FilePath.From("Foo.cs") });
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(dir.Replace('\\', '/'));

        try
        {
            // Cap at 2 matches
            var budgets = new BudgetLimits(maxResults: 2);
            var result = await _engine.SearchTextAsync(CommittedRouting(), "match", null, budgets);

            result.IsSuccess.Should().BeTrue();
            result.Value.Data.Truncated.Should().BeTrue();
            result.Value.Data.Matches.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SearchTextAsync_NoMatches_ReturnsTruncatedFalse()
    {
        _store.GetAllFilePathsAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(new List<FilePath>());
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns("C:/fake");

        var result = await _engine.SearchTextAsync(CommittedRouting(), "xyz123", null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Matches.Should().BeEmpty();
        result.Value.Data.Truncated.Should().BeFalse();
    }

    // ─── T01: Caching (PHASE-09-05) ───────────────────────────────────────────

    private static ResponseEnvelope<SearchTextResponse> MakeCachedEnvelope() =>
        new(
            "cached",
            new SearchTextResponse("x", [], 0, false),
            [],
            [],
            Confidence.High,
            new ResponseMeta(new TimingBreakdown(0), Sha,
                new Dictionary<string, LimitApplied>(), 0, 0m));

    [Fact]
    public async Task SearchTextAsync_CacheHit_SkipsDiskScan()
    {
        // Arrange: prime the cache with a pre-built envelope
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns("C:/fake");
        _cache.GetAsync<ResponseEnvelope<SearchTextResponse>>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeCachedEnvelope());

        // Act
        var result = await _engine.SearchTextAsync(CommittedRouting(), "pattern", null, null);

        // Assert: returned cached result; GetAllFilePathsAsync was never called (no disk I/O)
        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Pattern.Should().Be("x"); // from the cached envelope
        await _store.DidNotReceive()
            .GetAllFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchTextAsync_CacheMiss_PopulatesCache()
    {
        // Arrange: cache returns null (miss)
        _cache.GetAsync<ResponseEnvelope<SearchTextResponse>>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ResponseEnvelope<SearchTextResponse>?)null);
        _store.GetAllFilePathsAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns(new List<FilePath>());
        _store.GetRepoRootAsync(Repo, Sha, Arg.Any<CancellationToken>())
            .Returns("C:/fake");

        // Act
        await _engine.SearchTextAsync(CommittedRouting(), "pattern", null, null);

        // Assert: result was stored in the cache
        await _cache.Received(1).SetAsync(
            Arg.Any<string>(),
            Arg.Any<ResponseEnvelope<SearchTextResponse>>(),
            Arg.Any<CancellationToken>());
    }
}
