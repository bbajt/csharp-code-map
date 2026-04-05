namespace CodeMap.Harness.Repos;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;

/// <summary>
/// Parses optional harness configuration from ~/.codemap/harness-config.json.
/// Only Medium and Large tier repos need to be configured here; Micro and Small
/// are committed to the repository and always available.
/// </summary>
internal static class HarnessConfig
{
    /// <summary>
    /// Parses repos from JSON. Expected format:
    /// <code>
    /// {
    ///   "repos": [
    ///     {
    ///       "name": "MyLargeRepo",
    ///       "solution_path": "/path/to/Repo.sln",
    ///       "tier": "large",
    ///       "symbol_count_min": 50000,
    ///       "symbol_count_max": 500000,
    ///       "edge_count_min": 100000,
    ///       "edge_count_max": 2000000,
    ///       "fact_count_min": 100,
    ///       "known_query_inputs": ["MyClass", "IMyService"]
    ///     }
    ///   ]
    /// }
    /// </code>
    /// </summary>
    public static IReadOnlyList<RepoDescriptor> ParseRepos(string json)
    {
        var doc = JsonNode.Parse(json);
        var repos = doc?["repos"]?.AsArray();
        if (repos is null) return [];

        var result = new List<RepoDescriptor>();
        foreach (var item in repos)
        {
            if (item is null) continue;
            var descriptor = ParseDescriptor(item);
            if (descriptor is not null)
                result.Add(descriptor);
        }
        return result;
    }

    private static RepoDescriptor? ParseDescriptor(JsonNode node)
    {
        var name = node["name"]?.GetValue<string>();
        var solutionPath = node["solution_path"]?.GetValue<string>();
        var tierStr = node["tier"]?.GetValue<string>();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(solutionPath) || string.IsNullOrEmpty(tierStr))
            return null;

        var tier = tierStr.ToLowerInvariant() switch
        {
            "medium" => RepoTier.Medium,
            "large" => RepoTier.Large,
            _ => (RepoTier?)null,
        };
        if (tier is null) return null;

        var inputs = node["known_query_inputs"]?.AsArray()
            .Select(x => x?.GetValue<string>() ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList() ?? [];

        var gitRoot = node["git_root"]?.GetValue<string>();
        var syntheticRepoId = node["synthetic_repo_id"]?.GetValue<string>();

        return new RepoDescriptor(
            Name: name,
            SolutionPath: solutionPath,
            Tier: tier.Value,
            Anchors: [],
            CountExpectation: new IndexCountExpectation(
                SymbolCountMin: node["symbol_count_min"]?.GetValue<long>() ?? 0,
                SymbolCountMax: node["symbol_count_max"]?.GetValue<long>() ?? long.MaxValue,
                EdgeCountMin: node["edge_count_min"]?.GetValue<long>() ?? 0,
                EdgeCountMax: node["edge_count_max"]?.GetValue<long>() ?? long.MaxValue,
                FactCountMin: node["fact_count_min"]?.GetValue<long>() ?? 0
            ),
            KnownQueryInputs: inputs,
            GitRoot: gitRoot,
            SyntheticRepoId: syntheticRepoId
        );
    }
}
