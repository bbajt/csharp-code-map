namespace CodeMap.Integration.Tests.Storage;

using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// Findings §8.1 "Phase 0 test" (PHASE-21-06 T02): a baseline directory is self-contained. Copied into
/// another <c>CODEMAP_HOME</c>, it's adopted without a rebuild (<c>already_existed: true</c>) and serves
/// the same answers. This is the precondition for sharing baselines by copy, CI artifact or
/// <c>CODEMAP_CACHE_DIR</c>, and for ADR-053.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BaselineCopyBetweenRootsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-copy-roots-" + Path.GetRandomFileName());

    /// <summary>Removes the scratch clone and both data roots.</summary>
    public void Dispose() => AgentScratchRepo.DeleteDirectoryRobust(_root);

    [Fact]
    public async Task CopiedBaseline_IsAdoptedWithoutRebuild_AndAnswersIdentically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = KnownRepos.SampleSolution;
        var solutionRel = Path.GetRelativePath(repo.GitRepoRoot, repo.SolutionPath);
        await using var clone = await AgentScratchRepo.CreateAsync(repo.GitRepoRoot, Path.Combine(_root, "repo"), 1, ct);
        await clone.RestoreAsync(solutionRel, ct);
        var worktree = clone.Worktrees[0];
        var solution = Path.Combine(worktree, solutionRel);
        var homeA = Path.Combine(_root, "home-a");
        var homeB = Path.Combine(_root, "home-b");

        // 1. Build under root A and record the answers; stop the daemon so nothing maps the files.
        JsonNode?[] answersA;
        await using (var a = await StartDaemonAsync(homeA, "a", ct))
        {
            var built = await EnsureBaselineAsync(a, worktree, solution, ct);
            built["already_existed"]!.GetValue<bool>().Should().BeFalse("root A starts empty");
            answersA = await QueryAsync(a, worktree, ct);
        }

        // 2. Copy only the baseline directory into root B (same relative location).
        var sourceDir = Directory.GetDirectories(Path.Combine(homeA, "store"), clone.CommitSha, SearchOption.AllDirectories)
            .Should().ContainSingle().Subject;
        var targetDir = Path.Combine(homeB, Path.GetRelativePath(homeA, sourceDir));
        CopyDirectory(sourceDir, targetDir);

        // 3. Root B adopts it and answers identically.
        await using var b = await StartDaemonAsync(homeB, "b", ct);
        var adopted = await EnsureBaselineAsync(b, worktree, solution, ct);
        adopted["already_existed"]!.GetValue<bool>().Should().BeTrue("a complete copied baseline must not be rebuilt");
        var answersB = await QueryAsync(b, worktree, ct);

        for (var i = 0; i < answersA.Length; i++)
            JsonNode.DeepEquals(answersA[i], answersB[i]).Should().BeTrue(
                $"query {i} must answer identically from the copy.\nA: {answersA[i]?.ToJsonString()}\nB: {answersB[i]?.ToJsonString()}");
    }

    private static async Task<JsonNode> EnsureBaselineAsync(StdioMcpClient client, string worktree, string solution, CancellationToken ct)
    {
        var result = await client.CallToolAsync("index_ensure_baseline",
            new JsonObject { ["repo_path"] = worktree, ["solution_path"] = solution }, ct);
        result.Ok.Should().BeTrue($"{result.ErrorCode}: {result.ErrorMessage}");
        return result.PayloadJson()!;
    }

    /// <summary>The <c>data</c> of three answers that exercise the search index, refs and the graph.</summary>
    private static async Task<JsonNode?[]> QueryAsync(StdioMcpClient client, string worktree, CancellationToken ct)
    {
        var search = await CallDataAsync(client, "symbols_search",
            new JsonObject { ["repo_path"] = worktree, ["query"] = "Order", ["limit"] = 50 }, ct);
        var method = await CallDataAsync(client, "symbols_search",
            new JsonObject { ["repo_path"] = worktree, ["query"] = "Submit", ["kinds"] = new JsonArray("method") }, ct);
        var methodId = method?["hits"]?.AsArray().FirstOrDefault()?["symbol_id"]?.GetValue<string>();
        methodId.Should().NotBeNullOrEmpty("SampleSolution has a Submit method");

        var iface = await CallDataAsync(client, "symbols_search",
            new JsonObject { ["repo_path"] = worktree, ["query"] = "IOrderService", ["kinds"] = new JsonArray("interface") }, ct);
        var ifaceId = iface?["hits"]?.AsArray().FirstOrDefault()?["symbol_id"]?.GetValue<string>();
        ifaceId.Should().NotBeNullOrEmpty("SampleSolution declares IOrderService");

        var refs = await CallDataAsync(client, "refs_find",
            new JsonObject { ["repo_path"] = worktree, ["symbol_id"] = ifaceId }, ct);
        var callers = await CallDataAsync(client, "graph_callers",
            new JsonObject { ["repo_path"] = worktree, ["symbol_id"] = methodId }, ct);
        return [search, method, iface, refs, callers];
    }

    private static async Task<JsonNode?> CallDataAsync(StdioMcpClient client, string tool, JsonObject args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, ct);
        result.Ok.Should().BeTrue($"{tool}: {result.ErrorCode}: {result.ErrorMessage}");
        return result.PayloadJson()?["data"];
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private Task<StdioMcpClient> StartDaemonAsync(string home, string name, CancellationToken ct)
        => StdioMcpClient.StartAsync(
            ConcurrencyOptions.DefaultDaemonPath(),
            new Dictionary<string, string?> { ["CODEMAP_HOME"] = home, ["CODEMAP_CACHE_DIR"] = null },
            Path.Combine(_root, $"daemon-{name}.stderr.log"),
            TimeSpan.FromSeconds(180),
            ct);
}
