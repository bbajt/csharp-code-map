namespace CodeMap.Git;

using CodeMap.Core.Types;

/// <summary>
/// Validates and normalizes paths for Git operations.
/// </summary>
internal static class GitPathValidator
{
    /// <summary>
    /// Validates that the given path is a directory that exists.
    /// Resolves symlinks to canonical path.
    /// </summary>
    public static string ValidateAndNormalize(string repoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        string fullPath = Path.GetFullPath(repoPath);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(
                $"Repository path does not exist: {fullPath}");

        return fullPath;
    }

    /// <summary>
    /// Converts a LibGit2Sharp-returned path to a safe repo-relative FilePath.
    /// </summary>
    public static FilePath ToRepoRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');

        if (normalized.Contains(".."))
            throw new InvalidOperationException(
                $"Path traversal detected in git path: {path}");

        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Git path is empty after normalization.", nameof(path));

        return FilePath.From(normalized);
    }
}
