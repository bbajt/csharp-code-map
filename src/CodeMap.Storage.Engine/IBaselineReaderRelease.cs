namespace CodeMap.Storage.Engine;

/// <summary>
/// Lets <see cref="EngineBaselineScanner"/> close what this process holds open for a repo before it
/// deletes that repo's files (PHASE-21-10). On Windows a memory-mapped segment can't be deleted or
/// moved while mapped, so the scanner releases this process's handles first; anything still held
/// afterwards belongs to another process. Implemented by <see cref="CustomSymbolStore"/>.
/// </summary>
internal interface IBaselineReaderRelease
{
    /// <summary>
    /// Disposes this process's cached baseline readers for <paramref name="repoId"/> (all commits when
    /// <paramref name="commitSha"/> is null), and the overlays cached for the repo, which hold readers
    /// and their writer lock.
    /// </summary>
    void Release(string repoId, string? commitSha = null);
}
