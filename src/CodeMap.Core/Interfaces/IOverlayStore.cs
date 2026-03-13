namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Persistence layer for overlay (workspace-scoped, mutable) indexes.
/// Implementation: CodeMap.Storage.
/// </summary>
public interface IOverlayStore
{
    // === Write ===

    /// <summary>
    /// Creates an empty overlay DB for the given workspace, recording the baseline commit it tracks.
    /// Sets revision to 0. Idempotent — safe to call if overlay already exists.
    /// </summary>
    Task CreateOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CommitSha baselineCommitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a delta to the overlay: upserts files/symbols/refs from changed files,
    /// records deleted symbol IDs, and increments the revision counter.
    /// All writes are performed in a single transaction.
    /// </summary>
    Task ApplyDeltaAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        OverlayDelta delta,
        CancellationToken ct = default);

    /// <summary>
    /// Clears all data in the overlay (symbols, refs, files, deleted markers) and
    /// resets revision to 0. Preserves metadata (workspace_id, baseline_commit_sha).
    /// </summary>
    Task ResetOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>Deletes the overlay DB file entirely.</summary>
    Task DeleteOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    // === Read ===

    /// <summary>Returns true if an overlay DB exists for the given workspace.</summary>
    Task<bool> OverlayExistsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the current revision number of the overlay.
    /// Returns 0 if the overlay does not exist.
    /// </summary>
    Task<int> GetRevisionAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the overlay version of a symbol, or null if not present in the overlay.
    /// </summary>
    Task<SymbolCard?> GetOverlaySymbolAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>Full-text search across overlay symbols.</summary>
    Task<IReadOnlyList<SymbolSearchHit>> SearchOverlaySymbolsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        string query,
        SymbolSearchFilters? filters,
        int limit,
        CancellationToken ct = default);

    /// <summary>Gets references to a symbol from the overlay.</summary>
    Task<IReadOnlyList<StoredReference>> GetOverlayReferencesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        Enums.RefKind? kind,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the set of baseline symbol IDs that were deleted in this overlay.
    /// The query merge layer uses this to exclude them from baseline results.
    /// </summary>
    Task<IReadOnlySet<SymbolId>> GetDeletedSymbolIdsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the set of file paths that have been reindexed in this overlay.
    /// The query merge layer uses this to prefer overlay symbols for these files.
    /// </summary>
    Task<IReadOnlySet<FilePath>> GetOverlayFilePathsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns outgoing references from a symbol in the overlay —
    /// i.e. what the symbol calls. Queries refs WHERE from_symbol_id = symbolId.
    /// </summary>
    Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingOverlayReferencesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        Enums.RefKind? kind,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the base type and directly implemented interfaces for the given type symbol in the overlay.
    /// </summary>
    Task<IReadOnlyList<StoredTypeRelation>> GetOverlayTypeRelationsAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all types that extend or implement the given type symbol in the overlay.
    /// </summary>
    Task<IReadOnlyList<StoredTypeRelation>> GetOverlayDerivedTypesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the SemanticLevel stored in the overlay meta, or null if not available.
    /// </summary>
    Task<Enums.SemanticLevel?> GetOverlaySemanticLevelAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up a symbol by its stable structural fingerprint in the overlay.
    /// Returns null if the stable_id is not found.
    /// </summary>
    Task<SymbolCard?> GetSymbolByStableIdAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        Types.StableId stableId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all facts of the given kind from the overlay, ordered by value.
    /// </summary>
    Task<IReadOnlyList<StoredFact>> GetOverlayFactsByKindAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        Enums.FactKind kind,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all facts associated with a specific symbol from the overlay.
    /// Used to hydrate SymbolCard.Facts in workspace-mode GetSymbolCardAsync (PHASE-03-05).
    /// </summary>
    Task<IReadOnlyList<StoredFact>> GetOverlayFactsForSymbolAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of facts in the overlay.
    /// Used to populate FactCount in WorkspaceSummary (PHASE-03-07).
    /// </summary>
    Task<int> GetOverlayFactCountAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all unresolved edges from specific files in the overlay.
    /// Used by the resolution worker after successful recompilation.
    /// </summary>
    Task<IReadOnlyList<UnresolvedEdge>> GetOverlayUnresolvedEdgesAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        IReadOnlyList<FilePath> filePaths,
        CancellationToken ct = default);

    /// <summary>
    /// Upgrades an unresolved edge in the overlay to resolved.
    /// </summary>
    Task UpgradeOverlayEdgeAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        EdgeUpgrade upgrade,
        CancellationToken ct = default);
}
