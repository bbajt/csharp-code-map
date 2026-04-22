namespace CodeMap.Mcp.Context;

using System.Collections.Concurrent;

/// <summary>
/// Thread-safe default <see cref="IWorkspaceStickyRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Keys normalize repo paths the
/// same way <see cref="RepoRegistry"/> does so both registries agree on identity.
/// </summary>
public sealed class WorkspaceStickyRegistry : IWorkspaceStickyRegistry
{
    private readonly ConcurrentDictionary<string, string> _sticky =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Set(string repoPath, string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(workspaceId)) return;
        _sticky[Normalize(repoPath)] = workspaceId;
    }

    /// <inheritdoc/>
    public void Clear(string repoPath, string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(workspaceId)) return;
        var key = Normalize(repoPath);
        // Conditional remove: only clear if the sticky currently matches the deleted workspace.
        if (_sticky.TryGetValue(key, out var current) && string.Equals(current, workspaceId, StringComparison.Ordinal))
            _sticky.TryRemove(new KeyValuePair<string, string>(key, current));
    }

    /// <inheritdoc/>
    public string? Get(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;
        return _sticky.TryGetValue(Normalize(repoPath), out var ws) ? ws : null;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
