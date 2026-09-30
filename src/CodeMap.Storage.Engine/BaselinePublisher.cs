namespace CodeMap.Storage.Engine;

using System.Text.Json;

/// <summary>Outcome of <see cref="BaselinePublisher.Publish"/>.</summary>
internal enum PublishOutcome
{
    /// <summary>This call's staging directory became the baseline.</summary>
    Published,

    /// <summary>A complete baseline was already there (or won the race); staging was discarded.</summary>
    AdoptedExisting,
}

/// <summary>
/// Atomic, non-destructive publication of immutable baseline directories (ADR-040). Used by the
/// baseline builder and by shared-cache pull/push. A complete baseline is <b>never</b> deleted or
/// overwritten: another process may have its segments memory-mapped (F6, PHASE-21-02).
/// Concurrent publishers of the same (repo, commit) converge — losers adopt the winner's baseline,
/// which is equivalent because a baseline is a pure function of (repo, commit, solution, version).
/// </summary>
internal static class BaselinePublisher
{
    /// <summary>Prefix of directories moved aside by <see cref="Publish"/> under the quarantine root.</summary>
    internal const string QuarantinePrefix = "quarantine-";

    /// <summary>
    /// Prefix of baselines moved aside by <c>index_remove_repo</c> / <c>index_cleanup</c> before deletion
    /// (PHASE-21-10). A leftover means its delete failed; <see cref="SweepQuarantine"/> retries it.
    /// </summary>
    internal const string RemovedPrefix = "removed-";

    /// <summary>Every file a complete baseline contains — single source of truth for completeness.</summary>
    internal static readonly IReadOnlyList<string> RequiredFiles =
    [
        "manifest.json",
        "checksums.bin",
        "dictionary.seg",
        "search.idx",
        "content.seg",
        "symbols.seg",
        "files.seg",
        "projects.seg",
        "edges.seg",
        "facts.seg",
        "adjacency-out.idx",
        "adjacency-in.idx",
    ];

    /// <summary>
    /// True when <paramref name="baselineDir"/> exists, contains every <see cref="RequiredFiles"/>
    /// entry, and its manifest parses. A "gutted" baseline (segments missing) is not complete.
    /// </summary>
    internal static bool IsComplete(string baselineDir)
    {
        if (!Directory.Exists(baselineDir)) return false;
        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(baselineDir, file)))
                return false;
        }

        try
        {
            return ManifestWriter.Read(Path.Combine(baselineDir, "manifest.json")) is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Publishes the fully written <paramref name="stagingDir"/> as <paramref name="finalDir"/>.
    /// <list type="number">
    /// <item>Final already complete → discard staging, <see cref="PublishOutcome.AdoptedExisting"/>.</item>
    /// <item>Final exists but incomplete → rename it into <paramref name="quarantineRoot"/> (never delete —
    /// it may still be mapped); if it can't be moved, throw <see cref="StorageBusyException"/>.</item>
    /// <item>Atomic rename staging → final; if that loses a race to a complete peer baseline, adopt it.</item>
    /// </list>
    /// Staging, final and quarantine root must be on the same volume (rename, not copy).
    /// </summary>
    internal static PublishOutcome Publish(string stagingDir, string finalDir, string quarantineRoot)
    {
        if (IsComplete(finalDir))
        {
            DeleteBestEffort(stagingDir);
            return PublishOutcome.AdoptedExisting;
        }

        if (Directory.Exists(finalDir))
        {
            // Re-check: a peer may have published between the check above and Exists. Publication
            // is a single atomic rename, so a final dir that appeared in that window is complete —
            // quarantining it would move a live, correct baseline aside (seen on Linux, where
            // 16 racing publishers hit this window; the loser then failed with ENOTEMPTY).
            if (IsComplete(finalDir))
            {
                DeleteBestEffort(stagingDir);
                return PublishOutcome.AdoptedExisting;
            }

            var quarantine = Path.Combine(quarantineRoot,
                $"{QuarantinePrefix}{Path.GetFileName(finalDir)}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
            try
            {
                Directory.CreateDirectory(quarantineRoot);
                Directory.Move(finalDir, quarantine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A peer may have completed it in the meantime.
                if (IsComplete(finalDir))
                {
                    DeleteBestEffort(stagingDir);
                    return PublishOutcome.AdoptedExisting;
                }
                throw new StorageBusyException(
                    $"Baseline '{finalDir}' is incomplete and in use by another process; retry later.", ex);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
        try
        {
            Directory.Move(stagingDir, finalDir);
            return PublishOutcome.Published;
        }
        catch (IOException) when (IsComplete(finalDir))
        {
            // Lost the race between the checks above and the rename.
            DeleteBestEffort(stagingDir);
            return PublishOutcome.AdoptedExisting;
        }
    }

    /// <summary>
    /// Deletes quarantined directories under <paramref name="quarantineRoot"/>, skipping any that are
    /// still locked (they are retried on the next sweep). Best-effort; never throws.
    /// </summary>
    internal static void SweepQuarantine(string quarantineRoot)
    {
        if (!Directory.Exists(quarantineRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(quarantineRoot, QuarantinePrefix + "*")
                     .Concat(Directory.EnumerateDirectories(quarantineRoot, RemovedPrefix + "*")))
            DeleteBestEffort(dir);
    }

    /// <summary>
    /// Removes a baseline directory without ever leaving it half-deleted (PHASE-21-10). The directory is
    /// first renamed into <paramref name="quarantineRoot"/> in one step. If any segment is still open
    /// elsewhere the rename fails, and the baseline stays complete where it was. Only the moved-aside
    /// copy is then deleted, best effort; leftovers go with the next <see cref="SweepQuarantine"/>.
    /// </summary>
    /// <returns>True when the baseline was moved out of place (it is gone from its original path).</returns>
    internal static bool TryRemove(string baselineDir, string quarantineRoot)
    {
        if (!Directory.Exists(baselineDir)) return true;
        if (AnySegmentOpen(baselineDir)) return false;

        var target = Path.Combine(quarantineRoot,
            $"{RemovedPrefix}{Path.GetFileName(baselineDir)}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            Directory.CreateDirectory(quarantineRoot);
            Directory.Move(baselineDir, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        DeleteBestEffort(target);
        return true;
    }

    /// <summary>
    /// True when a file in <paramref name="baselineDir"/> can't be opened exclusively — another process
    /// has it open (a mapped segment). Checked before the rename because an open file doesn't always
    /// stop a directory rename.
    /// </summary>
    private static bool AnySegmentOpen(string baselineDir)
    {
        foreach (var file in Directory.EnumerateFiles(baselineDir))
        {
            try
            {
                using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }

    private static void DeleteBestEffort(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still mapped / locked by someone — leave it for a later sweep.
        }
    }
}
