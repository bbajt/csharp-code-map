namespace CodeMap.Harness.Repos;

using CodeMap.Core.Enums;

/// <summary>Tier classification for the repo suite.</summary>
public enum RepoTier { Micro, Small, Medium, Large }

/// <summary>
/// A single symbol that must exist in the index after indexing.
/// If any anchor is missing, the harness aborts before running queries.
/// </summary>
public record AnchorSymbol(
    string DisplayName,
    string StableIdPrefix,  // first 8 chars of stable_id, or exact FQN
    SymbolKind Kind
);

/// <summary>Expected symbol/edge/fact count ranges for a repo.</summary>
public record IndexCountExpectation(
    long SymbolCountMin,
    long SymbolCountMax,
    long EdgeCountMin,
    long EdgeCountMax,
    long FactCountMin
);

/// <summary>
/// Full descriptor for a repo used in the harness suite.
/// </summary>
public record RepoDescriptor(
    string Name,
    string SolutionPath,
    RepoTier Tier,
    IReadOnlyList<AnchorSymbol> Anchors,
    IndexCountExpectation CountExpectation,
    IReadOnlyList<string> KnownQueryInputs,
    string? GitRoot = null,
    string? SyntheticRepoId = null
)
{
    /// <summary>True for Micro and Small tiers — run in CI smoke mode.</summary>
    public bool IncludeInSmoke => Tier is RepoTier.Micro or RepoTier.Small;

    /// <summary>Directory containing the solution file. Used for file path normalization in baselines.</summary>
    public string RepoRoot => Path.GetDirectoryName(SolutionPath)!;

    /// <summary>
    /// Root of the git repository. Defaults to <see cref="RepoRoot"/>.
    /// Override when the solution lives inside a larger repo (e.g. testdata/ inside CodeMap).
    /// </summary>
    public string GitRepoRoot => GitRoot ?? RepoRoot;
}
