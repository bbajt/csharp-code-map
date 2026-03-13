namespace CodeMap.Roslyn;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

/// <summary>
/// Upgrades unresolved reference edges produced by syntactic fallback extraction
/// to resolved edges when a successful compilation becomes available.
///
/// The per-file method (<see cref="ResolveEdgesForFilesAsync"/>) is not on the
/// <see cref="IResolutionWorker"/> interface because it takes a Roslyn Compilation
/// and CodeMap.Core has zero dependencies (ADR-022 decision).
///
/// Strategy:
///   - Unique name match    → resolve
///   - Multiple matches     → try container hint to disambiguate
///   - Still ambiguous      → leave unresolved (don't guess)
/// </summary>
public sealed class ResolutionWorker : IResolutionWorker
{
    private readonly ILogger<ResolutionWorker> _logger;

    public ResolutionWorker(ILogger<ResolutionWorker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Batch resolution is deferred in M04. Returns 0.
    /// Per-file resolution via <see cref="ResolveEdgesForFilesAsync"/> covers the incremental case.
    /// </summary>
    public Task<int> ResolveEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>
    /// Storage-based overlay resolution — uses SearchSymbolsAsync on the baseline to find
    /// target symbols without needing the Roslyn Compilation object.
    /// Called automatically by WorkspaceManager after overlay refresh.
    /// </summary>
    public async Task<int> ResolveOverlayEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        WorkspaceId workspaceId,
        IReadOnlyList<FilePath> recompiledFiles,
        IOverlayStore overlayStore,
        ISymbolStore baselineStore,
        CancellationToken ct = default)
    {
        if (recompiledFiles.Count == 0) return 0;

        var edges = await overlayStore.GetOverlayUnresolvedEdgesAsync(
            repoId, workspaceId, recompiledFiles, ct);
        if (edges.Count == 0) return 0;

        int resolvedCount = 0;

        foreach (var edge in edges)
        {
            ct.ThrowIfCancellationRequested();
            if (edge.ToName is null) continue;

            var resolved = await TryResolveFromStoreAsync(
                repoId, commitSha, edge, baselineStore, ct);
            if (resolved is null) continue;

            await overlayStore.UpgradeOverlayEdgeAsync(repoId, workspaceId, new EdgeUpgrade(
                FromSymbolId: edge.FromSymbolId,
                FileId: edge.FileId,
                LocStart: edge.LocStart,
                ResolvedToSymbolId: SymbolId.From(resolved.SymbolId.Value),
                ResolvedStableToId: null), ct);  // StableId from search result not available

            resolvedCount++;
        }

        if (resolvedCount > 0)
            _logger.LogInformation(
                "Resolved {Count}/{Total} unresolved overlay edges for workspace {WorkspaceId}",
                resolvedCount, edges.Count, workspaceId.Value);

        return resolvedCount;
    }

    /// <summary>
    /// Resolve unresolved edges from specific files using the provided compilation.
    /// Called after successful recompilation of previously-failed files.
    /// Returns the count of edges successfully upgraded.
    ///
    /// Not on IResolutionWorker — callers that have a Compilation reference this directly.
    /// WorkspaceManager (in CodeMap.Query) calls this via overlay-store overload below.
    /// </summary>
    public async Task<int> ResolveEdgesForFilesAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<FilePath> filePaths,
        Compilation compilation,
        ISymbolStore store,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return 0;

        var edges = await store.GetUnresolvedEdgesAsync(repoId, commitSha, filePaths, ct);
        if (edges.Count == 0) return 0;

        var symbolsByName = BuildSymbolIndex(compilation);
        return await ResolveEdgesAsync(repoId, commitSha, edges, symbolsByName, store, ct);
    }

    /// <summary>
    /// Overlay variant: resolves unresolved edges from the overlay using the provided compilation.
    /// Upgrades edges in the overlay store; target symbol lookup uses the compilation.
    /// </summary>
    public async Task<int> ResolveOverlayEdgesForFilesAsync(
        RepoId repoId,
        CommitSha commitSha,
        WorkspaceId workspaceId,
        IReadOnlyList<FilePath> filePaths,
        Compilation compilation,
        IOverlayStore overlayStore,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return 0;

        var edges = await overlayStore.GetOverlayUnresolvedEdgesAsync(
            repoId, workspaceId, filePaths, ct);
        if (edges.Count == 0) return 0;

        var symbolsByName = BuildSymbolIndex(compilation);
        int resolvedCount = 0;

        foreach (var edge in edges)
        {
            ct.ThrowIfCancellationRequested();
            if (edge.ToName is null) continue;

            var resolved = TryResolve(edge, symbolsByName);
            if (resolved is null) continue;

            var resolvedId = SymbolId.From(
                resolved.GetDocumentationCommentId()
                ?? resolved.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            var stableId = SymbolFingerprinter.ComputeStableId(resolved);

            await overlayStore.UpgradeOverlayEdgeAsync(repoId, workspaceId, new EdgeUpgrade(
                FromSymbolId: edge.FromSymbolId,
                FileId: edge.FileId,
                LocStart: edge.LocStart,
                ResolvedToSymbolId: resolvedId,
                ResolvedStableToId: stableId), ct);

            resolvedCount++;
        }

        if (resolvedCount > 0)
            _logger.LogInformation(
                "Resolved {Count}/{Total} unresolved overlay edges for {FileCount} files",
                resolvedCount, edges.Count, filePaths.Count);

        return resolvedCount;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Storage-based resolution helper

    private static async Task<SymbolSearchHit?> TryResolveFromStoreAsync(
        RepoId repoId,
        CommitSha commitSha,
        UnresolvedEdge edge,
        ISymbolStore baselineStore,
        CancellationToken ct)
    {
        // SearchSymbolsAsync finds hits whose FQN contains the target name
        var hits = await baselineStore.SearchSymbolsAsync(
            repoId, commitSha, edge.ToName!, null, limit: 50, ct);

        // Filter to exact member-name matches (last segment before parenthesis)
        var exact = hits.Where(h => IsExactNameMatch(h.FullyQualifiedName, edge.ToName!)).ToList();

        if (exact.Count == 1) return exact[0];

        // Multiple matches — try container hint
        if (exact.Count > 1 && edge.ToContainerHint is { Length: > 0 } hint &&
            hint is not "this" and not "base")
        {
            var filtered = exact.Where(h =>
                h.FullyQualifiedName.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                h.FilePath.Value.Contains(hint, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (filtered.Count == 1) return filtered[0];
        }

        return null;  // Ambiguous or no match — don't guess
    }

    private static bool IsExactNameMatch(string fqName, string toName)
    {
        // FQN formats: "M:Namespace.Class.Method", "global::Namespace.Class.Method()"
        // Extract the member name: last segment before '(' and after last '.'
        var withoutParams = fqName.Split('(')[0];
        var lastDot = withoutParams.LastIndexOf('.');
        var lastColon = withoutParams.LastIndexOf(':');
        var nameStart = Math.Max(lastDot, lastColon) + 1;
        var memberName = nameStart > 0 && nameStart < withoutParams.Length
            ? withoutParams[nameStart..]
            : withoutParams;
        return string.Equals(memberName, toName, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared resolution logic

    private async Task<int> ResolveEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<UnresolvedEdge> edges,
        Dictionary<string, List<ISymbol>> symbolsByName,
        ISymbolStore store,
        CancellationToken ct)
    {
        int resolvedCount = 0;

        foreach (var edge in edges)
        {
            ct.ThrowIfCancellationRequested();
            if (edge.ToName is null) continue;

            var resolved = TryResolve(edge, symbolsByName);
            if (resolved is null) continue;

            var resolvedId = SymbolId.From(
                resolved.GetDocumentationCommentId()
                ?? resolved.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            var stableId = SymbolFingerprinter.ComputeStableId(resolved);

            await store.UpgradeEdgeAsync(repoId, commitSha, new EdgeUpgrade(
                FromSymbolId: edge.FromSymbolId,
                FileId: edge.FileId,
                LocStart: edge.LocStart,
                ResolvedToSymbolId: resolvedId,
                ResolvedStableToId: stableId), ct);

            resolvedCount++;
        }

        if (resolvedCount > 0)
            _logger.LogInformation(
                "Resolved {Count}/{Total} unresolved edges",
                resolvedCount, edges.Count);

        return resolvedCount;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Symbol index

    private static Dictionary<string, List<ISymbol>> BuildSymbolIndex(Compilation compilation)
    {
        var index = new Dictionary<string, List<ISymbol>>(StringComparer.Ordinal);
        foreach (var type in GetAllSourceTypes(compilation))
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                if (!index.TryGetValue(member.Name, out var list))
                    index[member.Name] = list = [];
                list.Add(member);
            }
        }
        return index;
    }

    private static ISymbol? TryResolve(
        UnresolvedEdge edge,
        Dictionary<string, List<ISymbol>> symbolsByName)
    {
        if (!symbolsByName.TryGetValue(edge.ToName!, out var candidates))
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        // Multiple candidates — try container hint
        if (edge.ToContainerHint is { Length: > 0 } hint &&
            hint is not "this" and not "base")
        {
            var filtered = candidates.Where(s =>
                s.ContainingType?.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) == true ||
                s.ContainingType?.ToDisplayString().Contains(hint, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();

            if (filtered.Count == 1)
                return filtered[0];
        }

        // Still ambiguous — don't guess
        return null;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllSourceTypes(Compilation compilation) =>
        GetTypesFromNamespace(compilation.Assembly.GlobalNamespace);

    private static IEnumerable<INamedTypeSymbol> GetTypesFromNamespace(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
                foreach (var t in GetTypesFromNamespace(childNs))
                    yield return t;
            else if (member is INamedTypeSymbol type)
                yield return type;
        }
    }
}
