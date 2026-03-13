namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Types;

/// <summary>
/// Manages a shared baseline cache — a shared filesystem directory where
/// pre-built baseline DB files can be pushed and pulled across machines/agents.
/// All operations are no-ops when the shared cache directory is not configured.
/// </summary>
public interface IBaselineCacheManager
{
    /// <summary>
    /// Returns true if the baseline for the given commit exists in the shared cache.
    /// Returns false if the shared cache is disabled.
    /// </summary>
    Task<bool> ExistsInCacheAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default);

    /// <summary>
    /// Copies a baseline from the shared cache to the local baseline directory.
    /// Returns the local path on success; null if the file is not in the cache,
    /// the cache is disabled, or the pulled file is corrupt.
    /// </summary>
    Task<string?> PullAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default);

    /// <summary>
    /// Copies the local baseline DB to the shared cache.
    /// No-op if the cache is disabled, the local baseline does not exist,
    /// or the baseline is already in the cache.
    /// </summary>
    Task PushAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default);
}
