namespace CodeMap.Harness.Repos;

using CodeMap.Core.Enums;

/// <summary>
/// Static descriptors for repos in each tier.
/// Micro and Small are committed to the repository.
/// Medium and Large are configured via ~/.codemap/harness-config.json (not committed).
/// </summary>
public static class KnownRepos
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>Micro tier — testdata/SampleSolution (~200 symbols). Deterministic, committed, fast.</summary>
    public static readonly RepoDescriptor SampleSolution = new(
        Name: "SampleSolution",
        SolutionPath: Path.Combine(RepoRoot, "testdata", "SampleSolution", "SampleSolution.sln"),
        Tier: RepoTier.Micro,
        Anchors:
        [
            new("IOrderService", "IOrderService", SymbolKind.Interface),
            new("OrderService", "OrderService", SymbolKind.Class),
        ],
        CountExpectation: new(
            SymbolCountMin: 50,
            SymbolCountMax: 2000,
            EdgeCountMin: 10,
            EdgeCountMax: 50000,
            FactCountMin: 5
        ),
        KnownQueryInputs:
        [
            "IOrderService",
            "OrderService",
            "Order",
            "Submit",
        ],
        GitRoot: RepoRoot,
        SyntheticRepoId: "harness-samplesolution"
    );

    /// <summary>Small tier — testdata/SampleVbSolution (~50 symbols). Cross-language (VB.NET).</summary>
    public static readonly RepoDescriptor SampleVbSolution = new(
        Name: "SampleVbSolution",
        SolutionPath: Path.Combine(RepoRoot, "testdata", "SampleVbSolution", "SampleVbSolution.sln"),
        Tier: RepoTier.Small,
        Anchors:
        [
            new("Module1", "Module1", SymbolKind.Class),
        ],
        CountExpectation: new(
            SymbolCountMin: 5,
            SymbolCountMax: 1000,
            EdgeCountMin: 0,
            EdgeCountMax: 10000,
            FactCountMin: 0
        ),
        KnownQueryInputs:
        [
            "Module1",
            "Main",
        ],
        GitRoot: RepoRoot,
        SyntheticRepoId: "harness-samplevbsolution"
    );

    /// <summary>Micro tier — testdata/SampleBlazorSolution. Blazor Server with @page, @inject, [Parameter].</summary>
    public static readonly RepoDescriptor SampleBlazorSolution = new(
        Name: "SampleBlazorSolution",
        SolutionPath: Path.Combine(RepoRoot, "testdata", "SampleBlazorSolution", "SampleBlazorSolution.slnx"),
        Tier: RepoTier.Micro,
        Anchors:
        [
            new("Counter", "Counter", SymbolKind.Class),
            new("MainLayout", "MainLayout", SymbolKind.Class),
            new("Greeting", "Greeting", SymbolKind.Class),
        ],
        CountExpectation: new(
            SymbolCountMin: 5,
            SymbolCountMax: 500,
            EdgeCountMin: 0,
            EdgeCountMax: 5000,
            FactCountMin: 5
        ),
        KnownQueryInputs:
        [
            "Counter",
            "Home",
            "Weather",
            "IncrementCount",
        ],
        GitRoot: RepoRoot,
        SyntheticRepoId: "harness-sampleblazorsolution"
    );

    /// <summary>All Micro + Small repos (Committed — always available).</summary>
    public static IReadOnlyList<RepoDescriptor> Committed =>
    [
        SampleSolution,
        SampleVbSolution,
        SampleBlazorSolution,
    ];

    /// <summary>
    /// Loads the Medium/Large repo descriptors from ~/.codemap/harness-config.json.
    /// Returns an empty list if the config file does not exist or the entry is unconfigured.
    /// </summary>
    public static IReadOnlyList<RepoDescriptor> LoadConfigured()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codemap", "harness-config.json");

        if (!File.Exists(configPath))
            return [];

        try
        {
            var json = File.ReadAllText(configPath);
            return HarnessConfig.ParseRepos(json);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns repos for a given tier name ("micro", "small", "medium", "large", "all").
    /// Returns null if the tier name is unknown.
    /// </summary>
    public static IReadOnlyList<RepoDescriptor>? ForTierName(string tierName)
    {
        var configured = LoadConfigured();
        return tierName.ToLowerInvariant() switch
        {
            "micro" => [SampleSolution, SampleBlazorSolution],
            "small" => [SampleVbSolution],
            "medium" => configured.Where(r => r.Tier == RepoTier.Medium).ToList(),
            "large" => configured.Where(r => r.Tier == RepoTier.Large).ToList(),
            "all" => [SampleSolution, SampleVbSolution, SampleBlazorSolution, .. configured],
            _ => null,
        };
    }

    private static string FindRepoRoot()
    {
        // Walk up from the executing assembly to find the repo root (contains CodeMap.sln)
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeMap.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate repo root (CodeMap.sln not found in any parent directory).");
    }
}
