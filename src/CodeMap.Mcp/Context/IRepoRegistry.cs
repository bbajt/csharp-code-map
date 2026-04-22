namespace CodeMap.Mcp.Context;

using CodeMap.Core.Errors;

/// <summary>
/// Per-process registry of repo paths the daemon has seen via
/// <c>index.ensure_baseline</c> or <c>workspace.create</c>. Used to auto-default the
/// <c>repo_path</c> argument when a session is working on a single repo.
/// </summary>
/// <remarks>
/// State is in-memory and per-process. A daemon restart clears the registry; agents
/// repopulate it implicitly by calling <c>index.ensure_baseline</c>, which is
/// idempotent and fast. This keeps the layer stateless-enough for concurrent clients
/// and avoids surprises where an old baseline silently becomes "default" at startup.
/// </remarks>
public interface IRepoRegistry
{
    /// <summary>Records a repo as open. Safe to call multiple times for the same path.</summary>
    void Register(string repoPath);

    /// <summary>Removes a repo from the registry. No-op if not present.</summary>
    void Forget(string repoPath);

    /// <summary>Snapshot of all known repo paths (normalized).</summary>
    IReadOnlyList<string> KnownRepos { get; }

    /// <summary>
    /// Resolves an argument value into a concrete repo path.
    /// <list type="bullet">
    ///   <item>Non-empty explicit path → normalized and returned verbatim.</item>
    ///   <item>Exactly one repo known → that repo.</item>
    ///   <item>Zero repos known → <c>INVALID_ARGUMENT</c> pointing to <c>index.ensure_baseline</c>.</item>
    ///   <item>Two+ repos known → <c>INVALID_ARGUMENT</c> listing known repos so the caller can choose.</item>
    /// </list>
    /// </summary>
    ResolveRepoResult Resolve(string? explicitRepoPath);
}

/// <summary>
/// Outcome of <see cref="IRepoRegistry.Resolve"/>. Exactly one of <see cref="RepoPath"/>
/// or <see cref="Error"/> is non-null.
/// </summary>
public readonly record struct ResolveRepoResult(string? RepoPath, CodeMapError? Error)
{
    /// <summary>True when resolution produced a concrete repo path.</summary>
    public bool IsSuccess => RepoPath is not null;
}
