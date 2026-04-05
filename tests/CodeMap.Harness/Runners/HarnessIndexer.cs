namespace CodeMap.Harness.Runners;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Queries;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;

/// <summary>
/// Shared indexing logic used by all runners.
/// Resolves repo identity, checks cache, compiles if needed, and returns the baseline commit.
/// </summary>
public sealed class HarnessIndexer(
    IGitService git,
    IRoslynCompiler compiler,
    ISymbolStore store,
    IBaselineCacheManager cache)
{
    /// <summary>
    /// Ensures a baseline exists for the repo. Returns (repoId, commitSha, alreadyExisted).
    /// On failure, reports to the reporter and returns nulls.
    /// </summary>
    public async Task<(RepoId? RepoId, CommitSha? CommitSha, bool AlreadyExisted)> IndexRepoAsync(
        RepoDescriptor repo,
        IHarnessReporter reporter,
        CancellationToken ct)
    {
        try
        {
            var repoId = repo.SyntheticRepoId is not null
                ? RepoId.From(repo.SyntheticRepoId)
                : await git.GetRepoIdentityAsync(repo.GitRepoRoot, ct).ConfigureAwait(false);
            var commitSha = await git.GetCurrentCommitAsync(repo.GitRepoRoot, ct).ConfigureAwait(false);

            var exists = await store.BaselineExistsAsync(repoId, commitSha, ct).ConfigureAwait(false);
            if (exists) return (repoId, commitSha, true);

            var pulled = await cache.PullAsync(repoId, commitSha, ct).ConfigureAwait(false);
            if (pulled is not null)
            {
                exists = await store.BaselineExistsAsync(repoId, commitSha, ct).ConfigureAwait(false);
                if (exists) return (repoId, commitSha, true);
            }

            var compilation = await compiler.CompileAndExtractAsync(repo.SolutionPath, ct).ConfigureAwait(false);
            await store.CreateBaselineAsync(repoId, commitSha, compilation, repo.RepoRoot, ct).ConfigureAwait(false);
            await cache.PushAsync(repoId, commitSha, ct).ConfigureAwait(false);
            return (repoId, commitSha, false);
        }
        catch (Exception ex)
        {
            reporter.ReportIndexFailed(repo, ex.Message);
            return (null, null, false);
        }
    }

    /// <summary>
    /// Sanitizes a query name for use as a golden file name on disk.
    /// </summary>
    public static string GoldenPath(string goldenDir, IHarnessQuery query)
    {
        var safe = query.Name
            .Replace(':', '.')
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace('*', '_')
            .TrimStart('.')
            .ToLowerInvariant();
        return Path.Combine(goldenDir, safe + ".json");
    }
}
