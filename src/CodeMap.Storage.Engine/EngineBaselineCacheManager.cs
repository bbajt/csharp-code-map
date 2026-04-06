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
    /// Shared cache directory, or <c>null</c> to disable caching (all ops become no-ops).
    /// </param>
    /// <param name="logger">Optional logger for cache operation warnings.</param>
    public EngineBaselineCacheManager(string storeBaseDir, string? sharedCacheDir,
        ILogger<EngineBaselineCacheManager>? logger = null)
    {
        _storeBaseDir = storeBaseDir;
        _sharedCacheDir = sharedCacheDir;
        _logger = logger ?? NullLogger<EngineBaselineCacheManager>.Instance;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsInCacheAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return Task.FromResult(false);
        var manifest = Path.Combine(GetCacheBaselineDir(repoId, commitSha), "manifest.json");
        return Task.FromResult(File.Exists(manifest));
    }

    /// <inheritdoc/>
    public async Task<string?> PullAsync(
        RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        if (_sharedCacheDir is null) return null;

        var cacheDir = GetCacheBaselineDir(repoId, commitSha);
        if (!File.Exists(Path.Combine(cacheDir, "manifest.json"))) return null;

        var localDir = GetLocalBaselineDir(repoId, commitSha);
        if (File.Exists(Path.Combine(localDir, "manifest.json"))) return localDir; // already local

        var tempDir = localDir + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await CopyDirectoryAsync(cacheDir, tempDir, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(localDir)!);
            if (Directory.Exists(localDir)) Directory.Delete(localDir, recursive: true);
            Directory.Move(tempDir, localDir);
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
        if (!File.Exists(Path.Combine(localDir, "manifest.json"))) return; // nothing to push

        var cacheDir = GetCacheBaselineDir(repoId, commitSha);
        if (File.Exists(Path.Combine(cacheDir, "manifest.json"))) return; // already cached

        var tempDir = cacheDir + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await CopyDirectoryAsync(localDir, tempDir, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
            Directory.Move(tempDir, cacheDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache push failed for {CommitSha}", commitSha.Value[..8]);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

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
