namespace CodeMap.Query;

using System.Collections.Concurrent;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates workspace lifecycle: create, refresh overlay, reset, delete, list.
/// Maintains an in-memory registry of active workspaces; overlay DB files persist on disk.
/// </summary>
public class WorkspaceManager
{
    private readonly IOverlayStore _overlayStore;
    private readonly IIncrementalCompiler _incrementalCompiler;
    private readonly ISymbolStore _baselineStore;
    private readonly IGitService _gitService;
    private readonly ICacheService _cacheService;
    private readonly IResolutionWorker _resolutionWorker;
    private readonly ILogger<WorkspaceManager> _logger;

    private readonly ConcurrentDictionary<(RepoId, WorkspaceId), WorkspaceInfo> _registry = new();

    public WorkspaceManager(
        IOverlayStore overlayStore,
        IIncrementalCompiler incrementalCompiler,
        ISymbolStore baselineStore,
        IGitService gitService,
        ICacheService cacheService,
        IResolutionWorker resolutionWorker,
        ILogger<WorkspaceManager> logger)
    {
        _overlayStore = overlayStore;
        _incrementalCompiler = incrementalCompiler;
        _baselineStore = baselineStore;
        _gitService = gitService;
        _cacheService = cacheService;
        _resolutionWorker = resolutionWorker;
        _logger = logger;
    }

    // ── CreateWorkspaceAsync ──────────────────────────────────────────────────

    /// <summary>
    /// Creates an overlay workspace anchored to an existing baseline commit.
    /// Idempotent: returns the current state if the workspace is already registered.
    /// </summary>
    /// <remarks>
    /// Requires the baseline to exist (checked via <c>BaselineExistsAsync</c>).
    /// The workspace is registered in the in-memory registry; the overlay DB file
    /// is created on disk by <see cref="Core.Interfaces.IOverlayStore"/>.
    /// <c>CreatedAt</c> is recorded in-memory only — lost on daemon restart.
    /// </remarks>
    public virtual async Task<Result<CreateWorkspaceResponse, CodeMapError>> CreateWorkspaceAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CommitSha baselineCommitSha,
        string solutionPath,
        string repoRootPath,
        CancellationToken ct = default)
    {
        // 1. Validate baseline exists
        var exists = await _baselineStore.BaselineExistsAsync(repoId, baselineCommitSha, ct)
                                         .ConfigureAwait(false);
        if (!exists)
            return Result<CreateWorkspaceResponse, CodeMapError>.Failure(
                new CodeMapError(
                    ErrorCodes.IndexNotAvailable,
                    $"Baseline index for {baselineCommitSha.Value} must exist before creating workspace. Call index.ensure_baseline first."));

        // 2. Idempotent — return current state if already registered
        var key = (repoId, workspaceId);
        if (_registry.TryGetValue(key, out var existing))
        {
            _logger.LogDebug("Workspace {WorkspaceId} already exists (revision {Rev})", workspaceId.Value, existing.CurrentRevision);
            return Result<CreateWorkspaceResponse, CodeMapError>.Success(
                new CreateWorkspaceResponse(workspaceId, existing.BaselineCommitSha, existing.CurrentRevision));
        }

        // 3. Create overlay DB
        await _overlayStore.CreateOverlayAsync(repoId, workspaceId, baselineCommitSha, ct)
                            .ConfigureAwait(false);

        // 4. Register in memory
        var info = new WorkspaceInfo(
            WorkspaceId: workspaceId,
            RepoId: repoId,
            BaselineCommitSha: baselineCommitSha,
            CurrentRevision: 0,
            SolutionPath: solutionPath,
            RepoRootPath: repoRootPath,
            CreatedAt: DateTimeOffset.UtcNow);
        _registry[key] = info;

        _logger.LogInformation("Workspace {WorkspaceId} created for repo {RepoId} at {Sha}",
            workspaceId.Value, repoId.Value, baselineCommitSha.Value[..8]);

        return Result<CreateWorkspaceResponse, CodeMapError>.Success(
            new CreateWorkspaceResponse(workspaceId, baselineCommitSha, 0));
    }

    // ── RefreshOverlayAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Reindexes changed files into the workspace overlay and runs resolution on new unresolved edges.
    /// </summary>
    /// <remarks>
    /// If <paramref name="explicitFilePaths"/> is provided, reindexes those files only.
    /// Otherwise, detects changes via <c>IGitService.GetChangedFilesAsync</c> relative to the
    /// workspace's baseline commit.
    /// Deleted files: gathers baseline symbols from those files and adds them to the deleted set.
    /// Resolution pass: skipped when <c>delta.SemanticLevel == SyntaxOnly</c> (no compilation).
    /// Invalidates the workspace cache prefix after applying the delta.
    /// </remarks>
    public virtual async Task<Result<RefreshOverlayResponse, CodeMapError>> RefreshOverlayAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        IReadOnlyList<FilePath>? explicitFilePaths,
        CancellationToken ct = default)
    {
        var key = (repoId, workspaceId);
        if (!_registry.TryGetValue(key, out var info))
            return Result<RefreshOverlayResponse, CodeMapError>.Failure(
                CodeMapError.NotFound("Workspace", workspaceId.Value));

        // 2. Determine changed files
        List<FilePath> changedFiles;
        List<FilePath> deletedFiles;

        if (explicitFilePaths is { Count: > 0 })
        {
            changedFiles = [.. explicitFilePaths];
            deletedFiles = [];
        }
        else
        {
            var fileChanges = await _gitService.GetChangedFilesAsync(info.RepoRootPath, info.BaselineCommitSha, ct)
                                               .ConfigureAwait(false);
            changedFiles = fileChanges
                .Where(fc => fc.Kind != FileChangeKind.Deleted)
                .Select(fc => fc.FilePath)
                .ToList();
            deletedFiles = fileChanges
                .Where(fc => fc.Kind == FileChangeKind.Deleted)
                .Select(fc => fc.FilePath)
                .ToList();
        }

        // 3. No changes detected
        if (changedFiles.Count == 0 && deletedFiles.Count == 0)
        {
            return Result<RefreshOverlayResponse, CodeMapError>.Success(
                new RefreshOverlayResponse(
                    FilesReindexed: 0,
                    SymbolsUpdated: 0,
                    NewOverlayRevision: info.CurrentRevision));
        }

        // 4. Get current revision
        var currentRevision = await _overlayStore.GetRevisionAsync(repoId, workspaceId, ct)
                                                  .ConfigureAwait(false);

        // 5. Compute delta for changed files (only if there are non-deleted files)
        OverlayDelta delta;
        if (changedFiles.Count > 0)
        {
            delta = await _incrementalCompiler.ComputeDeltaAsync(
                info.SolutionPath,
                info.RepoRootPath,
                changedFiles,
                _baselineStore,
                repoId,
                info.BaselineCommitSha,
                currentRevision,
                ct).ConfigureAwait(false);
        }
        else
        {
            delta = OverlayDelta.Empty(currentRevision + 1);
        }

        // 6. Handle deleted files — gather baseline symbols from those files
        if (deletedFiles.Count > 0)
        {
            var extraDeletedIds = new List<SymbolId>();
            foreach (var deletedFile in deletedFiles)
            {
                var baselineSymbols = await _baselineStore.GetSymbolsByFileAsync(
                    repoId, info.BaselineCommitSha, deletedFile, ct).ConfigureAwait(false);
                foreach (var sym in baselineSymbols)
                    extraDeletedIds.Add(sym.SymbolId);
            }

            if (extraDeletedIds.Count > 0)
            {
                // Merge extra deleted IDs into the delta (record is immutable — create new)
                var mergedDeleted = delta.DeletedSymbolIds.Concat(extraDeletedIds).ToList();
                delta = delta with { DeletedSymbolIds = mergedDeleted };
            }
        }

        // 7. Apply delta
        await _overlayStore.ApplyDeltaAsync(repoId, workspaceId, delta, ct).ConfigureAwait(false);

        // 7b. Resolution pass — upgrade any unresolved edges from syntactic fallback
        //     Uses storage-based symbol lookup (no Compilation needed in this layer).
        if (changedFiles.Count > 0 && delta.SemanticLevel != SemanticLevel.SyntaxOnly)
        {
            var resolvedCount = await _resolutionWorker.ResolveOverlayEdgesAsync(
                repoId, info.BaselineCommitSha, workspaceId,
                changedFiles, _overlayStore, _baselineStore, ct).ConfigureAwait(false);

            if (resolvedCount > 0)
                _logger.LogInformation(
                    "Resolution pass upgraded {Count} edges in workspace {WorkspaceId}",
                    resolvedCount, workspaceId.Value);
        }

        // 8. Invalidate workspace cache
        var prefix = $"{repoId.Value}:{info.BaselineCommitSha.Value}:ws:{workspaceId.Value}";
        await _cacheService.InvalidateAsync(prefix, ct).ConfigureAwait(false);

        // 9. Update in-memory state
        info = info with { CurrentRevision = delta.NewRevision };
        _registry[key] = info;

        _logger.LogInformation(
            "Overlay refreshed for workspace {WorkspaceId}: {FilesReindexed} files, {SymbolsUpdated} symbols, revision {Rev}",
            workspaceId.Value,
            changedFiles.Count + deletedFiles.Count,
            delta.AddedOrUpdatedSymbols.Count + delta.DeletedSymbolIds.Count,
            delta.NewRevision);

        return Result<RefreshOverlayResponse, CodeMapError>.Success(
            new RefreshOverlayResponse(
                FilesReindexed: changedFiles.Count + deletedFiles.Count,
                SymbolsUpdated: delta.AddedOrUpdatedSymbols.Count + delta.DeletedSymbolIds.Count,
                NewOverlayRevision: delta.NewRevision));
    }

    // ── ResetWorkspaceAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Resets the workspace overlay to revision 0, discarding all reindexed changes.
    /// Invalidates the workspace cache.
    /// </summary>
    public virtual async Task<Result<ResetWorkspaceResponse, CodeMapError>> ResetWorkspaceAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        var key = (repoId, workspaceId);
        if (!_registry.TryGetValue(key, out var info))
            return Result<ResetWorkspaceResponse, CodeMapError>.Failure(
                CodeMapError.NotFound("Workspace", workspaceId.Value));

        var previousRevision = info.CurrentRevision;

        await _overlayStore.ResetOverlayAsync(repoId, workspaceId, ct).ConfigureAwait(false);

        var prefix = $"{repoId.Value}:{info.BaselineCommitSha.Value}:ws:{workspaceId.Value}";
        await _cacheService.InvalidateAsync(prefix, ct).ConfigureAwait(false);

        info = info with { CurrentRevision = 0 };
        _registry[key] = info;

        _logger.LogInformation("Workspace {WorkspaceId} reset (was revision {Rev})", workspaceId.Value, previousRevision);

        return Result<ResetWorkspaceResponse, CodeMapError>.Success(
            new ResetWorkspaceResponse(workspaceId, previousRevision, NewRevision: 0));
    }

    // ── DeleteWorkspaceAsync ──────────────────────────────────────────────────

    /// <summary>
    /// Removes the workspace from the in-memory registry and deletes its overlay DB file.
    /// No-op if the workspace is not registered.
    /// </summary>
    public virtual async Task DeleteWorkspaceAsync(
        RepoId repoId,
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        _registry.TryRemove((repoId, workspaceId), out _);
        await _overlayStore.DeleteOverlayAsync(repoId, workspaceId, ct).ConfigureAwait(false);
    }

    // ── ListWorkspacesAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Lists all active workspaces for the given repo, enriched with staleness, semantic level,
    /// and fact count computed at query time.
    /// </summary>
    /// <remarks>
    /// <c>IsStale</c> is computed by comparing <c>BaselineCommitSha</c> against the current
    /// HEAD commit (via <c>IGitService.GetCurrentCommitAsync</c>). HEAD is cached per
    /// <c>repoRootPath</c> within the call to avoid redundant git invocations for the same repo.
    /// <c>CreatedAt</c> is in-memory only — not persisted across daemon restarts.
    /// </remarks>
    public virtual async Task<IReadOnlyList<WorkspaceSummary>> ListWorkspacesAsync(
        RepoId repoId,
        CancellationToken ct = default)
    {
        var workspaces = _registry
            .Where(kv => kv.Key.Item1 == repoId)
            .ToList();

        // Cache HEAD per repoRootPath to avoid redundant git calls for the same repo
        var headCache = new Dictionary<string, CommitSha>(StringComparer.Ordinal);

        var summaries = new List<WorkspaceSummary>(workspaces.Count);
        foreach (var kv in workspaces)
        {
            var info = kv.Value;

            var fileCount = (await _overlayStore.GetOverlayFilePathsAsync(
                repoId, info.WorkspaceId, ct).ConfigureAwait(false)).Count;

            var semanticLevel = await _overlayStore.GetOverlaySemanticLevelAsync(
                repoId, info.WorkspaceId, ct).ConfigureAwait(false);

            var factCount = await _overlayStore.GetOverlayFactCountAsync(
                repoId, info.WorkspaceId, ct).ConfigureAwait(false);

            if (!headCache.TryGetValue(info.RepoRootPath, out var currentHead))
            {
                currentHead = await _gitService.GetCurrentCommitAsync(info.RepoRootPath, ct)
                                               .ConfigureAwait(false);
                headCache[info.RepoRootPath] = currentHead;
            }

            summaries.Add(new WorkspaceSummary(
                WorkspaceId: info.WorkspaceId,
                BaseCommitSha: info.BaselineCommitSha,
                OverlayRevision: info.CurrentRevision,
                ModifiedFileCount: fileCount,
                IsStale: info.BaselineCommitSha != currentHead,
                SemanticLevel: semanticLevel,
                FactCount: factCount,
                CreatedAt: info.CreatedAt));
        }
        return summaries;
    }

    // ── GetStaleWorkspacesAsync ───────────────────────────────────────────────

    /// <summary>
    /// Returns workspaces whose <c>BaseCommitSha</c> no longer matches the repo HEAD.
    /// Convenience filter over <see cref="ListWorkspacesAsync"/>.
    /// </summary>
    public virtual async Task<IReadOnlyList<WorkspaceSummary>> GetStaleWorkspacesAsync(
        RepoId repoId,
        CancellationToken ct = default)
    {
        var all = await ListWorkspacesAsync(repoId, ct).ConfigureAwait(false);
        return all.Where(ws => ws.IsStale).ToList();
    }

    // ── GetWorkspaceInfo ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the in-memory state for the given workspace, or <c>null</c> if not registered.
    /// </summary>
    public virtual WorkspaceInfo? GetWorkspaceInfo(RepoId repoId, WorkspaceId workspaceId) =>
        _registry.TryGetValue((repoId, workspaceId), out var info) ? info : null;
}

// ── Response records ─────────────────────────────────────────────────────────

/// <summary>Response from a successful workspace creation.</summary>
public record CreateWorkspaceResponse(
    WorkspaceId WorkspaceId,
    CommitSha BaselineCommitSha,
    int CurrentRevision);

/// <summary>Response from a successful overlay refresh operation.</summary>
public record RefreshOverlayResponse(
    int FilesReindexed,
    int SymbolsUpdated,
    int NewOverlayRevision);

/// <summary>Response from a successful workspace reset operation.</summary>
public record ResetWorkspaceResponse(
    WorkspaceId WorkspaceId,
    int PreviousRevision,
    int NewRevision);

/// <summary>
/// Summary of an active workspace returned by <c>workspace.list</c>.
/// <see cref="IsStale"/> is computed at query time by comparing <see cref="BaseCommitSha"/>
/// against the current repo HEAD. <see cref="CreatedAt"/> is in-memory only.
/// </summary>
public record WorkspaceSummary(
    WorkspaceId WorkspaceId,
    CommitSha BaseCommitSha,
    int OverlayRevision,
    int ModifiedFileCount,
    bool IsStale = false,
    SemanticLevel? SemanticLevel = null,
    int FactCount = 0,
    DateTimeOffset? CreatedAt = null);

/// <summary>Response from <c>workspace.list</c> — includes current HEAD commit for staleness comparison.</summary>
public record WorkspaceListResponse(
    IReadOnlyList<WorkspaceSummary> Workspaces,
    CommitSha CurrentCommitSha);

/// <summary>Response from <c>workspace.delete</c>.</summary>
public record WorkspaceDeleteResponse(
    WorkspaceId WorkspaceId,
    bool Deleted);
