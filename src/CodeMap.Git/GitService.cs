namespace CodeMap.Git;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

/// <summary>
/// Read-only Git repository state provider backed by LibGit2Sharp.
/// Each method opens and disposes its own Repository instance (stateless).
/// </summary>
public sealed class GitService : IGitService
{
    private readonly ILogger<GitService> _logger;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// RepoId derivation: SHA-256 of normalized remote origin URL (first 16 hex chars).
    /// If no remote exists, uses SHA-256 of the normalized absolute repo root path,
    /// prefixed with "local-" to distinguish local-only repos.
    /// URL normalization: lowercase, strip trailing ".git" and "/".
    /// </remarks>
    public Task<RepoId> GetRepoIdentityAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);
        RepoId repoId = DeriveRepoId(repo);

        _logger.LogInformation("Resolved repo identity {RepoId} for {RepoPath}", repoId, repoPath);
        return Task.FromResult(repoId);
    }

    /// <inheritdoc/>
    /// <remarks>Throws <see cref="InvalidOperationException"/> if HEAD is unborn (no commits yet).</remarks>
    public Task<CommitSha> GetCurrentCommitAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        if (repo.Head.Tip is null)
            throw new InvalidOperationException("Repository has no commits (unborn HEAD).");

        return Task.FromResult(CommitSha.From(repo.Head.Tip.Sha));
    }

    /// <inheritdoc/>
    /// <remarks>Returns "HEAD" when the repository is in detached HEAD state.</remarks>
    public Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        string branch = repo.Head.FriendlyName;
        if (branch == "(no branch)" || repo.Info.IsHeadDetached)
            branch = "HEAD";

        return Task.FromResult(branch);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Two-pass diff: (1) baseline tree vs HEAD tree (committed changes since baseline),
    /// (2) HEAD tree vs working directory (uncommitted index/working-copy changes).
    /// Working-directory changes override committed-change entries for the same path.
    /// </remarks>
    public Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoPath,
        CommitSha baseline,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        var baselineCommit = repo.Lookup<Commit>(baseline.Value)
            ?? throw new InvalidOperationException(
                $"Commit {baseline.Value} not found in repository.");

        // Track changes: path → FileChangeKind, working directory overrides committed
        var changes = new Dictionary<string, FileChangeKind>(StringComparer.Ordinal);

        // 1. Diff baseline tree vs HEAD tree (committed changes since baseline)
        if (repo.Head.Tip is not null)
        {
            var committedDiff = repo.Diff.Compare<TreeChanges>(
                baselineCommit.Tree,
                repo.Head.Tip.Tree);

            foreach (TreeEntryChanges entry in committedDiff)
            {
                string key = entry.OldPath ?? entry.Path;
                changes[entry.Path] = MapChangeKind(entry.Status);
            }

            // 2. Diff HEAD tree vs working directory (uncommitted changes)
            var workingDiff = repo.Diff.Compare<TreeChanges>(
                repo.Head.Tip.Tree,
                DiffTargets.Index | DiffTargets.WorkingDirectory);

            foreach (TreeEntryChanges entry in workingDiff)
            {
                changes[entry.Path] = MapChangeKind(entry.Status);
            }
        }

        var result = changes
            .Select(kvp => new FileChange(GitPathValidator.ToRepoRelativePath(kvp.Key), kvp.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<FileChange>>(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Includes untracked files in the dirty check. An unborn repository (no commits) is treated as clean.
    /// </remarks>
    public Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        // Empty repo (no commits) with no files — consider clean
        if (repo.Head.Tip is null)
            return Task.FromResult(true);

        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        });

        return Task.FromResult(!status.IsDirty);
    }

    // --- Private helpers ---

    private static RepoId DeriveRepoId(Repository repo)
    {
        string? remoteUrl = repo.Network.Remotes["origin"]?.Url
                            ?? repo.Network.Remotes.FirstOrDefault()?.Url;

        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            string normalized = NormalizeRemoteUrl(remoteUrl);
            string hash = Sha256Hex(normalized)[..16];
            return RepoId.From(hash);
        }

        string repoRoot = repo.Info.WorkingDirectory ?? repo.Info.Path;
        string normalizedPath = NormalizePath(repoRoot);
        string pathHash = Sha256Hex(normalizedPath)[..16];
        return RepoId.From($"local-{pathHash}");
    }

    private static string NormalizeRemoteUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        url = url.TrimEnd('/');
        return url.ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        path = Path.GetFullPath(path);
        path = path.Replace('\\', '/');
        path = path.TrimEnd('/');
        return path.ToLowerInvariant();
    }

    private static string Sha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static FileChangeKind MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => FileChangeKind.Added,
        ChangeKind.Modified => FileChangeKind.Modified,
        ChangeKind.Deleted => FileChangeKind.Deleted,
        ChangeKind.Renamed => FileChangeKind.Renamed,
        ChangeKind.Copied => FileChangeKind.Added,
        ChangeKind.TypeChanged => FileChangeKind.Modified,
        _ => FileChangeKind.Modified,
    };
}
