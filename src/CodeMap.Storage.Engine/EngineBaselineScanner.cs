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
    private readonly IBaselineReaderRelease? _release;

    /// <param name="storeBaseDir">
    /// Root store directory (same value passed to <see cref="CustomSymbolStore"/>).
    /// </param>
    public EngineBaselineScanner(string storeBaseDir)
        : this(storeBaseDir, (IBaselineReaderRelease?)null)
    {
    }

    /// <summary>
    /// Creates a scanner that releases <paramref name="store"/>'s open readers and overlays for a repo
    /// before deleting its files (PHASE-21-10).
    /// </summary>
    public EngineBaselineScanner(string storeBaseDir, CustomSymbolStore store)
        : this(storeBaseDir, (IBaselineReaderRelease)store)
    {
    }

    /// <summary>Creates a scanner with an explicit release hook (tests).</summary>
    internal EngineBaselineScanner(string storeBaseDir, IBaselineReaderRelease? release)
    {
        _storeBaseDir = storeBaseDir;
        _release = release;
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
        var repoDir = Path.Combine(_storeBaseDir, SanitizeRepoId(repoId.Value));

        // Close what this process holds for the repo; a lock or mapping still held afterwards belongs
        // to another process. Released readers/overlays reopen lazily if they are used again.
        _release?.Release(repoId.Value);

        var baselines = await ListBaselinesAsync(repoId, ct).ConfigureAwait(false);
        var overlays = ListOverlayDirs(repoDir);
        var inUse = overlays.Where(o => IsOverlayHeld(o.Dir)).Select(o => o.Name).ToList();
        var idle = overlays.Where(o => !inUse.Contains(o.Name)).ToList();

        if (dryRun || inUse.Count > 0)
        {
            // Dry run: what would go. Live workspace elsewhere (PHASE-21-10): nothing is deleted.
            var wouldRemove = dryRun ? baselines : [];
            return new RemoveRepoResponse(
                repoId,
                wouldRemove.Count,
                wouldRemove.Sum(b => b.SizeBytes),
                wouldRemove.Select(b => b.CommitSha).ToList(),
                dryRun,
                WorkspacesRemoved: dryRun && idle.Count > 0 ? idle.Select(o => o.Name).ToList() : null,
                WorkspacesInUse: inUse.Count > 0 ? inUse : null);
        }

        var workspacesRemoved = new List<string>();
        foreach (var overlay in idle)
        {
            if (DeleteDirectory(overlay.Dir))
                workspacesRemoved.Add(overlay.Name);
        }

        var quarantineRoot = Path.Combine(repoDir, "temp");
        var removed = new List<BaselineInfo>();
        var skipped = new List<CommitSha>();
        foreach (var baseline in baselines)
        {
            var dir = Path.Combine(repoDir, "baselines", baseline.CommitSha.Value);
            if (BaselinePublisher.TryRemove(dir, quarantineRoot))
                removed.Add(baseline);
            else
                skipped.Add(baseline.CommitSha);
        }

        // Nothing of the repo is left in use: drop the (now empty or temp-only) repo directory.
        if (skipped.Count == 0 && workspacesRemoved.Count == idle.Count)
            DeleteDirectory(repoDir);

        return new RemoveRepoResponse(
            repoId,
            removed.Count,
            removed.Sum(b => b.SizeBytes),
            removed.Select(b => b.CommitSha).ToList(),
            DryRun: false,
            WorkspacesRemoved: workspacesRemoved.Count > 0 ? workspacesRemoved : null,
            SkippedInUse: skipped.Count > 0 ? skipped : null);
    }

    /// <summary>Workspace overlay directories of a repo (<c>store/&lt;repo&gt;/overlays/&lt;ws&gt;/</c>).</summary>
    private static IReadOnlyList<(string Name, string Dir)> ListOverlayDirs(string repoDir)
    {
        var overlaysDir = Path.Combine(repoDir, "overlays");
        if (!Directory.Exists(overlaysDir)) return [];
        return Directory.GetDirectories(overlaysDir)
            .Select(d => (Path.GetFileName(d), d))
            .OrderBy(o => o.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True when another process holds the overlay's ADR-044 writer lock (held for the overlay's whole
    /// lifetime). The probe opens it with <see cref="FileShare.None"/>, which .NET enforces on Unix too.
    /// </summary>
    private static bool IsOverlayHeld(string overlayDir)
    {
        var lockFile = Path.Combine(overlayDir, "overlay.lock");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Recursive delete that reports success instead of throwing.</summary>
    private static bool DeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        var repoDir = Path.Combine(_storeBaseDir, SanitizeRepoId(repoId.Value));
        if (!dryRun)
            _release?.Release(repoId.Value);   // this process's readers must not block its own cleanup

        var baselines = await ListBaselinesAsync(repoId, ct).ConfigureAwait(false);

        // Workspaces of every process, via the markers written at workspace_create (PHASE-21-10).
        var markerShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withoutRecord = new List<string>();
        var liveWithoutRecord = new List<string>();
        foreach (var (name, dir) in ListOverlayDirs(repoDir))
        {
            if (OverlayMarker.IsBaselineLevel(name)) continue;   // not a workspace
            if (OverlayMarker.TryReadBaseline(dir) is { } sha)
                markerShas.Add(sha);
            else if (IsOverlayHeld(dir))
                liveWithoutRecord.Add(name);
            else
                withoutRecord.Add(name);
        }

        var protectedShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentHead.Value };
        foreach (var ws in workspaceBaseCommits)
            protectedShas.Add(ws.Value);
        protectedShas.UnionWith(markerShas);

        var workspaceShas = new HashSet<string>(markerShas, StringComparer.OrdinalIgnoreCase);
        workspaceShas.UnionWith(workspaceBaseCommits.Select(c => c.Value));
        var protectedByWorkspaces = baselines
            .Where(b => workspaceShas.Contains(b.CommitSha.Value) && !b.CommitSha.Value.Equals(currentHead.Value, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.CommitSha)
            .ToList();

        // A live workspace whose baseline isn't recorded (pre-v2.10 daemon) could be based on any
        // baseline: delete nothing.
        if (!dryRun && liveWithoutRecord.Count > 0)
        {
            return new CleanupResponse(0, 0, [], baselines.Select(b => b.CommitSha).ToList(), DryRun: false,
                ProtectedByWorkspaces: protectedByWorkspaces.Count > 0 ? protectedByWorkspaces : null,
                WorkspacesWithoutBaselineRecord: withoutRecord.Count > 0 ? withoutRecord : null,
                WorkspacesInUse: liveWithoutRecord);
        }

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
        var skipped = new List<CommitSha>();

        if (!dryRun)
        {
            var quarantineRoot = Path.Combine(repoDir, "temp");
            foreach (var baseline in candidates)
            {
                var dir = Path.Combine(repoDir, "baselines", baseline.CommitSha.Value);
                if (!BaselinePublisher.TryRemove(dir, quarantineRoot))
                {
                    skipped.Add(baseline.CommitSha);   // in use elsewhere: left complete
                    continue;
                }
                bytesReclaimed += baseline.SizeBytes;
                removed.Add(baseline.CommitSha);

                // The baseline-level overlay of a removed commit is useless; drop it unless it is held.
                var levelOverlay = Path.Combine(repoDir, "overlays", baseline.CommitSha.Value);
                if (Directory.Exists(levelOverlay) && !IsOverlayHeld(levelOverlay))
                    DeleteDirectory(levelOverlay);
            }
        }
        else
        {
            bytesReclaimed = candidates.Sum(b => b.SizeBytes);
            removed = candidates.Select(b => b.CommitSha).ToList();
        }

        if (!dryRun)
            SweepLegacyOverlays(_storeBaseDir);

        var removedSet = removed.Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = baselines
            .Where(b => !removedSet.Contains(b.CommitSha.Value))
            .Select(b => b.CommitSha)
            .ToList();

        return new CleanupResponse(removed.Count, bytesReclaimed, removed, kept, dryRun,
            ProtectedByWorkspaces: protectedByWorkspaces.Count > 0 ? protectedByWorkspaces : null,
            SkippedInUse: skipped.Count > 0 ? skipped : null,
            WorkspacesWithoutBaselineRecord: withoutRecord.Count > 0 ? withoutRecord : null,
            WorkspacesInUse: liveWithoutRecord.Count > 0 ? liveWithoutRecord : null);
    }

    /// <summary>
    /// Best-effort removal of the pre-v2.8.2 flat overlay tree <c>&lt;store&gt;/overlays/&lt;ws&gt;/</c>,
    /// which nothing reads since overlays became repo-scoped (ADR-043). A directory whose
    /// <c>overlay.wal</c> is still held by a running (older) daemon is skipped: the probe opens
    /// it with <see cref="FileShare.None"/>, which .NET enforces on Unix too, so the sweep never
    /// deletes a live overlay on any OS. The root is removed once empty.
    /// </summary>
    internal static void SweepLegacyOverlays(string storeBaseDir)
    {
        var legacyRoot = Path.Combine(storeBaseDir, "overlays");
        if (!Directory.Exists(legacyRoot)) return;

        foreach (var dir in Directory.GetDirectories(legacyRoot))
        {
            try
            {
                var wal = Path.Combine(dir, "overlay.wal");
                if (File.Exists(wal))
                {
                    using var probe = new FileStream(wal, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* held by a running daemon, or not ours to delete — skip */ }
        }

        try
        {
            if (Directory.GetFileSystemEntries(legacyRoot).Length == 0)
                Directory.Delete(legacyRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* best-effort */ }
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
