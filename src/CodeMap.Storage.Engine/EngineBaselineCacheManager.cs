namespace CodeMap.Storage.Engine;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// IBaselineCacheManager for the v2 custom storage engine.
/// Caches baseline directories (not .db files) in a shared filesystem path.
/// Each cached entry is a directory: <c>{cacheDir}/{repoId}/{commitSha}/</c>.
/// Replaces the SQLite-based BaselineCacheManager from CodeMap.Storage.
/// </summary>
public sealed class EngineBaselineCacheManager : IBaselineCacheManager
{
    private readonly string _storeBaseDir;
    private readonly string? _sharedCacheDir;
    private readonly ILogger<EngineBaselineCacheManager> _logger;

    /// <param name="storeBaseDir">
    /// Local store directory (same value passed to <see cref="CustomSymbolStore"/>).
    /// </param>
    /// <param name="sharedCacheDir">
    /// Shared cache directory, or <c>null</c>/blank to disable caching (all ops become no-ops).
    /// </param>
    /// <param name="logger">Optional logger for cache operation warnings.</param>
    public EngineBaselineCacheManager(string storeBaseDir, string? sharedCacheDir,
        ILogger<EngineBaselineCacheManager>? logger = null)
    {
        _storeBaseDir = storeBaseDir;
        // Blank = disabled (F11): Path.Combine("", repo, sha) is relative, so a blank dir used to
        // publish baselines into the process's working directory.
        _sharedCacheDir = string.IsNullOrWhiteSpace(sharedCacheDir) ? null : sharedCacheDir;
        _logger = logger ?? NullLogger<EngineBaselineCacheManager>.Instance;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsInCacheAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return Task.FromResult(false);
        return Task.FromResult(BaselinePublisher.IsComplete(GetCacheBaselineDir(repoId, commitSha)));
    }

    /// <inheritdoc/>
    public async Task<string?> PullAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return null;

        var cacheDir = GetCacheBaselineDir(repoId, commitSha);
        if (!BaselinePublisher.IsComplete(cacheDir)) return null;

        var localDir = GetLocalBaselineDir(repoId, commitSha);
        if (BaselinePublisher.IsComplete(localDir)) return localDir; // already local

        var tempDir = localDir + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await CopyDirectoryAsync(cacheDir, tempDir, ct).ConfigureAwait(false);
            // Non-destructive publish (ADR-040): never delete a local baseline another
            // process may have mapped; adopt one that appeared concurrently.
            BaselinePublisher.Publish(tempDir, localDir, LocalQuarantineRoot(repoId));
            return localDir;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache pull failed for {CommitSha}", commitSha.Value[..8]);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task PushAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return;

        var localDir = GetLocalBaselineDir(repoId, commitSha);
        if (!BaselinePublisher.IsComplete(localDir)) return; // nothing (complete) to push

        var cacheDir = GetCacheBaselineDir(repoId, commitSha);
        if (BaselinePublisher.IsComplete(cacheDir)) return; // already cached

        var tempDir = cacheDir + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await CopyDirectoryAsync(localDir, tempDir, ct).ConfigureAwait(false);
            // Non-destructive publish (ADR-040): the shared cache may be read by other
            // machines/processes; adopt a concurrently pushed copy instead of replacing it.
            BaselinePublisher.Publish(tempDir, cacheDir, CacheQuarantineRoot(repoId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache push failed for {CommitSha}", commitSha.Value[..8]);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Quarantine root for a local baseline: <c>&lt;store&gt;/&lt;repo&gt;/temp</c> — the same directory
    /// the builder stages in and sweeps (same volume as the target).
    /// </summary>
    private string LocalQuarantineRoot(RepoId repoId)
        => Path.Combine(_storeBaseDir, SanitizeSegment(repoId.Value), "temp");

    /// <summary>Quarantine root for a shared-cache entry: <c>&lt;cache&gt;/&lt;repo&gt;/temp</c>.</summary>
    private string CacheQuarantineRoot(RepoId repoId)
        => Path.Combine(_sharedCacheDir!, SanitizeSegment(repoId.Value), "temp");

    private string GetLocalBaselineDir(RepoId repoId, CommitSha commitSha)
        => Path.Combine(_storeBaseDir, SanitizeSegment(repoId.Value), "baselines", commitSha.Value);

    private string GetCacheBaselineDir(RepoId repoId, CommitSha commitSha)
        => Path.Combine(_sharedCacheDir!, SanitizeSegment(repoId.Value), commitSha.Value);

    private static async Task CopyDirectoryAsync(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            using var src = File.OpenRead(file);
            using var dst = File.Create(destFile);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
}
