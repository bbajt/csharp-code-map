namespace CodeMap.Storage.Engine;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// IBaselineScanner implementation for the v2 custom storage engine.
/// Scans <c>{storeBaseDir}/{repoId}/baselines/{commitSha}/</c> directories.
/// Replaces the SQLite-based BaselineDbFactory from CodeMap.Storage.
/// </summary>
public sealed class EngineBaselineScanner : IBaselineScanner
{
    private readonly string _storeBaseDir;

    /// <param name="storeBaseDir">
    /// Root store directory (same value passed to <see cref="CustomSymbolStore"/>).
    /// </param>
    public EngineBaselineScanner(string storeBaseDir)
    {
        _storeBaseDir = storeBaseDir;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BaselineInfo>> ListBaselinesAsync(
        RepoId repoId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var baselines = new List<BaselineInfo>();
        var baselinesDir = Path.Combine(_storeBaseDir, SanitizeRepoId(repoId.Value), "baselines");

        if (!Directory.Exists(baselinesDir))
            return Task.FromResult<IReadOnlyList<BaselineInfo>>([]);

        foreach (var dir in Directory.GetDirectories(baselinesDir))
        {
            ct.ThrowIfCancellationRequested();

            var sha = Path.GetFileName(dir);
            if (sha.Length != 40 || !IsHexString(sha)) continue;

            var manifestPath = Path.Combine(dir, "manifest.json");
            var manifest = ManifestWriter.Read(manifestPath);
            if (manifest is null) continue;

            long sizeBytes = 0;
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                    sizeBytes += new FileInfo(file).Length;
            }
            catch { /* best-effort */ }

            baselines.Add(new BaselineInfo(
                CommitSha: CommitSha.From(sha.ToLowerInvariant()),
                CreatedAt: manifest.CreatedAt,
                SizeBytes: sizeBytes,
                IsCurrentHead: false,        // Caller enriches
                IsActiveWorkspaceBase: false  // Caller enriches
            ));
        }

        return Task.FromResult<IReadOnlyList<BaselineInfo>>(
            baselines.OrderByDescending(b => b.CreatedAt).ToList());
    }

    /// <inheritdoc/>
    public async Task<RemoveRepoResponse> RemoveRepoAsync(
        RepoId repoId,
        bool dryRun = true,
        CancellationToken ct = default)
    {
        var baselines = await ListBaselinesAsync(repoId, ct).ConfigureAwait(false);
        long bytesFreed = baselines.Sum(b => b.SizeBytes);
        var commits = baselines.Select(b => b.CommitSha).ToList();

        if (!dryRun)
        {
            var repoDir = Path.Combine(_storeBaseDir, SanitizeRepoId(repoId.Value));
            if (Directory.Exists(repoDir))
            {
                try { Directory.Delete(repoDir, recursive: true); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                { /* best-effort */ }
            }
        }

        return new RemoveRepoResponse(repoId, commits.Count, bytesFreed, commits, dryRun);
    }

    /// <inheritdoc/>
    public async Task<CleanupResponse> CleanupBaselinesAsync(
        RepoId repoId,
        CommitSha currentHead,
        IReadOnlySet<CommitSha> workspaceBaseCommits,
        int keepCount = 5,
        int? olderThanDays = null,
        bool dryRun = true,
        CancellationToken ct = default)
    {
        var baselines = await ListBaselinesAsync(repoId, ct).ConfigureAwait(false);

        var protectedShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentHead.Value };
        foreach (var ws in workspaceBaseCommits)
            protectedShas.Add(ws.Value);

        var candidates = baselines
            .Where(b => !protectedShas.Contains(b.CommitSha.Value))
            .ToList();

        if (olderThanDays.HasValue)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays.Value);
            candidates = candidates.Where(b => b.CreatedAt < cutoff).ToList();
        }

        var keepShas = baselines
            .OrderByDescending(b => b.CreatedAt)
            .Take(keepCount)
            .Select(b => b.CommitSha.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        candidates = candidates.Where(b => !keepShas.Contains(b.CommitSha.Value)).ToList();

        long bytesReclaimed = 0;
        var removed = new List<CommitSha>();

        if (!dryRun)
        {
            foreach (var baseline in candidates)
            {
                var dir = Path.Combine(
                    _storeBaseDir, SanitizeRepoId(repoId.Value), "baselines", baseline.CommitSha.Value);
                try
                {
                    Directory.Delete(dir, recursive: true);
                    bytesReclaimed += baseline.SizeBytes;
                    removed.Add(baseline.CommitSha);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                { /* best-effort */ }
            }
        }
        else
        {
            bytesReclaimed = candidates.Sum(b => b.SizeBytes);
            removed = candidates.Select(b => b.CommitSha).ToList();
        }

        var removedSet = removed.Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = baselines
            .Where(b => !removedSet.Contains(b.CommitSha.Value))
            .Select(b => b.CommitSha)
            .ToList();

        return new CleanupResponse(removed.Count, bytesReclaimed, removed, kept, dryRun);
    }

    private static bool IsHexString(string value)
    {
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static string SanitizeRepoId(string repoId)
    {
        var chars = repoId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        return new string(chars);
    }
}
