namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Provides read-only access to Git repository state.
/// Implementation: CodeMap.Git.
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Gets a stable identifier for the repository.
    /// Derived from the first remote URL, or a hash of the absolute path if no remote.
    /// </summary>
    Task<RepoId> GetRepoIdentityAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Gets the SHA of the current HEAD commit.</summary>
    Task<CommitSha> GetCurrentCommitAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Gets the current branch name (e.g., "main", "feature/xyz").</summary>
    Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Gets the list of files changed between the given baseline commit and
    /// the current working tree.
    /// </summary>
    Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoPath,
        CommitSha baseline,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if the working tree has no uncommitted changes.
    /// </summary>
    Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Resolves a commitish (short SHA, branch name, tag, etc.) to a full 40-char commit SHA.
    /// Returns null if the commitish cannot be resolved.
    /// </summary>
    Task<CommitSha?> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct = default);
}
