namespace CodeMap.Roslyn;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

/// <summary>
/// Groups Roslyn <see cref="Project"/> instances returned by
/// <c>MSBuildWorkspace</c> by their underlying <c>.csproj</c> file path.
/// A multi-targeted project (<c>net8.0;net9.0;net10.0</c>) yields one Project
/// per TFM with names like <c>MyLib(net8.0)</c>; this helper collapses them
/// into a single <see cref="ProjectGroup"/> so extraction runs once per csproj.
/// </summary>
internal static class RoslynProjectGrouping
{
    /// <summary>
    /// One logical csproj's worth of compilations. For single-target projects
    /// this is a group of one; for multi-targeted projects it contains every
    /// TFM variant <c>solution.Projects</c> returned, sorted by canonical-TFM
    /// rank (highest first).
    /// </summary>
    public sealed record ProjectGroup(
        string CanonicalName,
        string? FilePath,
        IReadOnlyList<Project> AllProjects,
        IReadOnlyList<string> TargetFrameworks);

    // Match the trailing parenthetical only when its content actually looks like
    // a TFM (starts with net / netstandard / netcoreapp). This protects user-named
    // projects like "My (Backup) Lib" or "Tooling (legacy)" from having their
    // parenthetical misclassified as a TFM and leaked into ProjectDiagnostic.
    private static readonly Regex _tfmInName =
        new(@"\((?<tfm>(?:net|netstandard|netcoreapp)[^)]*)\)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Groups <paramref name="projects"/> by <see cref="Project.FilePath"/>
    /// (case-insensitive on Windows). Each group's projects are pre-ranked by
    /// canonical-TFM order — highest TFM first — so callers can pick the
    /// canonical compilation by reading <c>group.AllProjects[0]</c> and
    /// fall back through later entries on compile failure.
    /// </summary>
    public static IReadOnlyList<ProjectGroup> GroupByFilePath(IEnumerable<Project> projects)
    {
        var result = new List<ProjectGroup>();

        // Projects with no FilePath cannot be merged with anything (defensive
        // — happens with in-memory test compilations only). Treat each such
        // project as its own group, keyed by ProjectId to keep them distinct.
        var byPath = projects
            .GroupBy(p => p.FilePath ?? $"<no-path:{p.Id.Id:D}>",
                     StringComparer.OrdinalIgnoreCase);

        foreach (var pathGroup in byPath)
        {
            var ranked = pathGroup.OrderByDescending(RankProject).ToList();
            var canonicalName = StripTfm(ranked[0].Name);
            var tfms = ranked.Select(p => ParseTfm(p.Name) ?? "(unknown)").ToList();
            string? filePath = ranked[0].FilePath;
            result.Add(new ProjectGroup(canonicalName, filePath, ranked, tfms));
        }

        return result;
    }

    /// <summary>
    /// Parses the trailing parenthetical TFM token from a Roslyn project name
    /// like <c>"MudBlazor(net10.0)"</c>. Returns <c>null</c> when the name has
    /// no parenthetical (single-target project).
    /// </summary>
    public static string? ParseTfm(string projectName)
    {
        var match = _tfmInName.Match(projectName);
        return match.Success ? match.Groups["tfm"].Value : null;
    }

    /// <summary>
    /// Removes the trailing TFM parenthetical from a Roslyn project name.
    /// <c>"MudBlazor(net10.0)"</c> → <c>"MudBlazor"</c>. Names without a
    /// parenthetical are returned unchanged.
    /// </summary>
    public static string StripTfm(string projectName)
    {
        var match = _tfmInName.Match(projectName);
        return match.Success ? projectName[..match.Index].TrimEnd() : projectName;
    }

    /// <summary>
    /// Ranks a <see cref="Project"/> by its TFM. Higher rank = better canonical
    /// candidate. See <see cref="RankTfm(string?)"/> for the scoring scheme.
    /// </summary>
    public static long RankProject(Project project) => RankTfm(ParseTfm(project.Name));

    /// <summary>
    /// Ranks a TFM string. Order:
    /// <list type="number">
    ///   <item>SDK family (Core/5+ &gt; netcoreapp &gt; netstandard &gt; .NET Framework)</item>
    ///   <item>Within family, version major.minor descending</item>
    /// </list>
    /// Unknown / unparseable TFMs rank lowest. OS-suffixed TFMs
    /// (<c>net10.0-windows10.0.19041.0</c>) parse the family/version part only.
    /// </summary>
    public static long RankTfm(string? tfm)
    {
        if (string.IsNullOrEmpty(tfm)) return 0;

        var lower = tfm.ToLowerInvariant();
        // Strip OS suffix (e.g. "net10.0-windows10.0.19041.0" → "net10.0").
        int dash = lower.IndexOf('-');
        if (dash >= 0) lower = lower[..dash];

        if (lower.StartsWith("netstandard", StringComparison.Ordinal))
            return 1_000_000 + ParseVersionScore(lower["netstandard".Length..]);

        if (lower.StartsWith("netcoreapp", StringComparison.Ordinal))
            return 2_000_000 + ParseVersionScore(lower["netcoreapp".Length..]);

        if (lower.StartsWith("net", StringComparison.Ordinal))
        {
            var v = lower["net".Length..];
            // Compact net4x form (no dot): net48, net472, net461, etc. — Framework.
            if (!v.Contains('.', StringComparison.Ordinal) && int.TryParse(v, out var compact))
                return 500_000 + compact;
            // Dotted form: net5.0, net10.0, etc. — current SDK family.
            return 3_000_000 + ParseVersionScore(v);
        }

        return 0;
    }

    private static long ParseVersionScore(string version)
    {
        var parts = version.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major)) return 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        return major * 1_000 + minor;
    }
}
