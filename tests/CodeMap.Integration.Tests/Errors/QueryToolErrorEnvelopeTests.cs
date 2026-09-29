namespace CodeMap.Integration.Tests.Errors;

using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// End-to-end pin for PHASE-21-04 T02 (ADR-045): a read-only tool never answers a storage problem
/// with JSON-RPC <c>-32603</c>. Scenario: a baseline that lost a segment (the F6 "gutted" shape) is
/// queried by <c>symbols_search</c> in a fresh daemon.
/// The query path pre-checks completeness (ADR-040), so the agent gets <c>INDEX_NOT_AVAILABLE</c>
/// naming <c>index_ensure_baseline</c>. (The spec assumed this surfaced as an exception → -32603; it
/// never did. Escaped exceptions are covered in-process by <c>McpServerTests</c>.)
/// </summary>
[Trait("Category", "Integration")]
public sealed class QueryToolErrorEnvelopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-query-errors-" + Path.GetRandomFileName());

    /// <summary>Removes the scratch clone and data root.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_root);

    [Fact]
    public async Task SymbolsSearch_GuttedBaseline_ReturnsIndexNotAvailableNamingEnsureBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = KnownRepos.SampleSolution;
        var home = Path.Combine(_root, "home");
        await using var clone = await AgentScratchRepo.CreateAsync(repo.GitRepoRoot, Path.Combine(_root, "repo"), 1, ct);
        var worktree = clone.Worktrees[0];
        var solution = Path.Combine(worktree, Path.GetRelativePath(repo.GitRepoRoot, repo.SolutionPath));

        // 1. Index with one daemon, then stop it so nothing maps the baseline.
        await using (var indexer = await StartDaemonAsync(home, "indexer", ct))
        {
            var built = await indexer.CallToolAsync("index_ensure_baseline",
                new JsonObject { ["repo_path"] = worktree, ["solution_path"] = solution }, ct);
            built.Ok.Should().BeTrue($"{built.ErrorCode}: {built.ErrorMessage}");
        }

        // 2. Gut it: remove one required segment.
        var baselineDir = Directory.GetDirectories(Path.Combine(home, "store"), clone.CommitSha, SearchOption.AllDirectories)
            .Should().ContainSingle().Subject;
        File.Delete(Path.Combine(baselineDir, "search.idx"));

        // 3. A fresh daemon serves a read-only tool from it.
        await using var reader = await StartDaemonAsync(home, "reader", ct);
        var search = await reader.CallToolAsync("symbols_search",
            new JsonObject { ["repo_path"] = worktree, ["query"] = "Order" }, ct);

        search.Ok.Should().BeFalse("a gutted baseline must never be served (ADR-040)");
        search.ErrorCode.Should().Be("INDEX_NOT_AVAILABLE",
            $"the agent needs an actionable CodeMap error, never -32603 (got {search.ErrorCode}: {search.ErrorMessage})");
        search.ErrorMessage.Should().Contain("index_ensure_baseline");
    }

    private Task<StdioMcpClient> StartDaemonAsync(string home, string name, CancellationToken ct)
        => StdioMcpClient.StartAsync(
            ConcurrencyOptions.DefaultDaemonPath(),
            new Dictionary<string, string?> { ["CODEMAP_HOME"] = home, ["CODEMAP_CACHE_DIR"] = null },
            Path.Combine(_root, $"daemon-{name}.stderr.log"),
            TimeSpan.FromSeconds(180),
            ct);
}
