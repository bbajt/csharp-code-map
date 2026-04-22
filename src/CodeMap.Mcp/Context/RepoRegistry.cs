namespace CodeMap.Mcp.Context;

using System.Collections.Concurrent;
using CodeMap.Core.Errors;

/// <summary>
/// Thread-safe default <see cref="IRepoRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Normalizes paths on insert to
/// deduplicate <c>"./Foo"</c>, <c>"Foo"</c>, and <c>"Foo/"</c>.
/// </summary>
public sealed class RepoRegistry : IRepoRegistry
{
    private readonly ConcurrentDictionary<string, byte> _repos =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Register(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return;
        _repos.TryAdd(Normalize(repoPath), 0);
    }

    /// <inheritdoc/>
    public void Forget(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return;
        _repos.TryRemove(Normalize(repoPath), out _);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> KnownRepos => _repos.Keys.ToList();

    /// <inheritdoc/>
    public ResolveRepoResult Resolve(string? explicitRepoPath)
    {
        // Explicit path wins, verbatim — downstream (Git) handles its own path canonicalization.
        if (!string.IsNullOrWhiteSpace(explicitRepoPath))
            return new ResolveRepoResult(explicitRepoPath, null);

        var known = _repos.Keys.ToList();
        return known.Count switch
        {
            1 => new ResolveRepoResult(known[0], null),
            0 => new ResolveRepoResult(null, CodeMapError.InvalidArgument(
                "repo_path is required — no repo has been indexed yet. Run index.ensure_baseline first.")),
            _ => new ResolveRepoResult(null, CodeMapError.InvalidArgument(
                $"repo_path is required — {known.Count} repos are indexed: {string.Join(", ", known)}. " +
                "Pass one explicitly.")),
        };
    }

    /// <summary>
    /// Normalizes for registry storage only: absolute path, forward slashes, no trailing slash.
    /// Explicit <c>repo_path</c> arguments are returned by <see cref="Resolve"/> verbatim; this
    /// normalization is used only to dedupe registry keys so <c>Foo</c> and <c>./Foo/</c>
    /// resolve to the same entry.
    /// </summary>
    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
