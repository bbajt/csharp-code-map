namespace CodeMap.Roslyn;

using Microsoft.CodeAnalysis;

/// <summary>
/// Finds a project's NuGet restore output (<c>project.assets.json</c>) without evaluating MSBuild
/// (F12, PHASE-21-07 T02). MSBuildWorkspace doesn't restore and reports nothing when the assets file
/// is missing (probed: no workspace diagnostic), so a fresh clone or git worktree compiles with
/// unresolved references. This probe makes that visible in <c>ProjectDiagnostic.MissingRestoreOutput</c>.
/// </summary>
internal static class RestoreOutputProbe
{
    private const string AssetsFileName = "project.assets.json";

    /// <summary>Levels walked up from the intermediate assembly's directory: RID, TFM, configuration, obj root.</summary>
    private const int MaxWalkUpLevels = 4;

    /// <summary>
    /// Null when the project's <c>project.assets.json</c> exists, or when the project doesn't use NuGet
    /// restore. Otherwise the <paramref name="solutionDir"/>-relative path where it was expected
    /// (<c>&lt;projectDir&gt;/obj/project.assets.json</c>, forward slashes).
    /// </summary>
    internal static string? FindMissing(Project project, string solutionDir) =>
        FindMissing(project.FilePath, project.CompilationOutputInfo.AssemblyPath, solutionDir);

    /// <summary>
    /// Testable core of <see cref="FindMissing(Project, string)"/>. Looks in
    /// <c>&lt;projectDir&gt;/obj/</c>, then walks up at most <see cref="MaxWalkUpLevels"/> levels from the
    /// intermediate assembly's directory (covers <c>BaseIntermediateOutputPath</c> overrides and the
    /// .NET 8+ artifacts layout), never above <paramref name="solutionDir"/>.
    /// </summary>
    internal static string? FindMissing(string? projectFilePath, string? intermediateAssemblyPath, string solutionDir)
    {
        if (string.IsNullOrEmpty(projectFilePath) || !UsesRestore(projectFilePath))
            return null;

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
        var expected = Path.Combine(projectDir, "obj", AssetsFileName);
        if (File.Exists(expected))
            return null;

        if (!string.IsNullOrEmpty(intermediateAssemblyPath))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(solutionDir));
            var dir = Path.GetDirectoryName(Path.GetFullPath(intermediateAssemblyPath));
            for (var level = 0; level <= MaxWalkUpLevels && dir is not null && IsWithin(dir, root); level++)
            {
                if (File.Exists(Path.Combine(dir, AssetsFileName)))
                    return null;
                dir = Path.GetDirectoryName(dir);
            }
        }

        return Path.GetRelativePath(solutionDir, expected).Replace('\\', '/');
    }

    /// <summary>
    /// True when the project uses NuGet restore: an SDK-style project (<c>Sdk</c> attribute or
    /// <c>&lt;Sdk&gt;</c> element) or any project with a <c>PackageReference</c>. Legacy
    /// <c>packages.config</c> / reference-only projects never have an assets file. Unreadable → false
    /// (never flag what can't be checked).
    /// </summary>
    internal static bool UsesRestore(string projectFilePath)
    {
        string text;
        try { text = File.ReadAllText(projectFilePath); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        return text.Contains("Sdk=\"", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Sdk='", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<Sdk ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithin(string dir, string root) =>
        dir.Equals(root, StringComparison.OrdinalIgnoreCase)
        || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
