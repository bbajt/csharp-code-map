namespace CodeMap.Roslyn;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Incrementally recompiles only the projects containing changed files
/// and diffs the new symbols against the baseline to produce an <see cref="OverlayDelta"/>.
///
/// Caches the loaded MSBuildWorkspace and Solution between calls.
/// On subsequent calls for the same solution, uses Solution.WithDocumentText to
/// apply file changes incrementally, avoiding the ~1.4s MSBuildWorkspace startup cost.
/// </summary>
public class IncrementalCompiler : IIncrementalCompiler
{
    private readonly SymbolDiffer _differ;
    private readonly ILogger<IncrementalCompiler> _logger;

    // Workspace caching — protects the cached solution from concurrent callers
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MSBuildWorkspace? _cachedWorkspace;
    private Solution? _cachedSolution;
    private string? _cachedSolutionPath;

    public IncrementalCompiler(SymbolDiffer differ, ILogger<IncrementalCompiler> logger)
    {
        _differ = differ;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Acquires <c>SemaphoreSlim(1)</c> before entering the core compile path to prevent
    /// concurrent solution mutations on the cached workspace.
    /// Cold path (~1.4s): opens a fresh <c>MSBuildWorkspace</c> and loads the solution.
    /// Warm path (~63ms): reuses cached workspace, applies changed files via
    /// <c>Solution.WithDocumentText</c>, then recompiles only affected projects.
    /// </remarks>
    public async Task<OverlayDelta> ComputeDeltaAsync(
        string solutionPath,
        string repoRootPath,
        IReadOnlyList<FilePath> changedFiles,
        ISymbolStore baselineStore,
        RepoId repoId,
        CommitSha commitSha,
        int currentRevision,
        CancellationToken ct = default)
    {
        if (changedFiles.Count == 0)
        {
            _logger.LogDebug("No changed files — returning empty delta");
            return OverlayDelta.Empty(currentRevision + 1);
        }

        MsBuildInitializer.EnsureRegistered();

        await _lock.WaitAsync(ct);
        try
        {
            return await ComputeDeltaCoreAsync(
                solutionPath, repoRootPath, changedFiles,
                baselineStore, repoId, commitSha, currentRevision, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<OverlayDelta> ComputeDeltaCoreAsync(
        string solutionPath,
        string repoRootPath,
        IReadOnlyList<FilePath> changedFiles,
        ISymbolStore baselineStore,
        RepoId repoId,
        CommitSha commitSha,
        int currentRevision,
        CancellationToken ct)
    {
        Solution solution;

        // Build set of absolute paths for changed files
        var changedAbsolute = changedFiles
            .Select(f => NormalizePath(Path.Combine(repoRootPath, f.Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_cachedSolution is not null && _cachedSolutionPath == solutionPath)
        {
            // === WARM PATH — reuse cached workspace, update changed documents ===
            _logger.LogDebug("Using cached solution for incremental compile: {Path}", solutionPath);
            solution = _cachedSolution;

            // Build path → DocumentId index once (O(D)) rather than scanning per changed file (O(N×D))
            var docByPath = BuildDocumentPathIndex(solution);

            foreach (var changedFile in changedFiles)
            {
                var absolutePath = NormalizePath(Path.Combine(repoRootPath, changedFile.Value));
                if (!File.Exists(absolutePath)) continue;

                if (!docByPath.TryGetValue(absolutePath, out var documentId))
                {
                    _logger.LogDebug("Document not found in solution for {Path}", absolutePath);
                    continue;
                }

                var newText = SourceText.From(await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false));
                solution = solution.WithDocumentText(documentId, newText);
            }
        }
        else
        {
            // === COLD PATH — create a fresh workspace ===
            _logger.LogInformation("Opening solution for incremental compile: {Path}", solutionPath);

            _cachedWorkspace?.Dispose();
            _cachedWorkspace = null;
            _cachedSolution = null;

            var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler((args) =>
                _logger.LogWarning("Workspace diagnostic [{Kind}]: {Message}",
                    args.Diagnostic.Kind, args.Diagnostic.Message));

            solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
            _cachedWorkspace = workspace;
            _cachedSolutionPath = solutionPath;
        }

        // Cache the (possibly document-updated) solution for the next call
        _cachedSolution = solution;

        // Identify which projects contain any of the changed files
        var affectedProjectIds = new HashSet<ProjectId>();
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null) continue;
                if (changedAbsolute.Contains(NormalizePath(doc.FilePath)))
                {
                    affectedProjectIds.Add(project.Id);
                    break;
                }
            }
        }

        if (affectedProjectIds.Count == 0)
        {
            _logger.LogInformation("No projects affected by changed files — returning empty delta");
            return OverlayDelta.Empty(currentRevision + 1);
        }

        _logger.LogInformation(
            "Recompiling {Count} affected project(s) for {FileCount} changed file(s)",
            affectedProjectIds.Count, changedFiles.Count);

        // Recompile affected projects and collect symbols/refs/files/facts from changed files only
        var newSymbols = new List<SymbolCard>();
        var newRefs = new List<ExtractedReference>();
        var newFiles = new List<ExtractedFile>();
        var newFacts = new List<ExtractedFact>();

        string normalizedDir = repoRootPath.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var projectId in affectedProjectIds)
        {
            ct.ThrowIfCancellationRequested();

            var project = solution.GetProject(projectId)!;
            var compilation = await project.GetCompilationAsync(ct);

            if (compilation is null)
            {
                _logger.LogWarning("Compilation returned null for project {Project}", project.Name);
                continue;
            }

            // Extract all symbols, refs, and facts from this project
            var projectSymbols = SymbolExtractor.ExtractAll(compilation, project.Name, repoRootPath);
            var projectRefs = ReferenceExtractor.ExtractAll(compilation, repoRootPath);

            // Skip fact extraction for test/benchmark projects (same logic as RoslynCompiler)
            bool isTestProject = project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || project.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)
                || project.Name.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase);

            var projectFacts = isTestProject
                ? []
                : EndpointExtractor.ExtractAll(compilation, repoRootPath)
                    .Concat(ConfigKeyExtractor.ExtractAll(compilation, repoRootPath))
                    .Concat(DbTableExtractor.ExtractAll(compilation, repoRootPath))
                    .Concat(DiRegistrationExtractor.ExtractAll(compilation, repoRootPath))
                    .Concat(MiddlewareExtractor.ExtractAll(compilation, repoRootPath))
                    .Concat(RetryPolicyExtractor.ExtractAll(compilation, repoRootPath))
                    .Concat(ExceptionExtractor.ExtractAll(compilation, repoRootPath))
                    .Concat(LogExtractor.ExtractAll(compilation, repoRootPath))
                    .ToList();

            // Filter to only changed files
            var filteredSymbols = projectSymbols
                .Where(s => changedAbsolute.Contains(
                    NormalizePath(Path.Combine(repoRootPath, s.FilePath.Value))))
                .ToList();
            var filteredFacts = projectFacts
                .Where(f => changedAbsolute.Contains(
                    NormalizePath(Path.Combine(repoRootPath, f.FilePath.Value))))
                .ToList();
            var filteredRefs = projectRefs
                .Where(r => changedAbsolute.Contains(
                    NormalizePath(Path.Combine(repoRootPath, r.FilePath.Value))))
                .ToList();

            newSymbols.AddRange(filteredSymbols);
            newRefs.AddRange(filteredRefs);
            newFacts.AddRange(filteredFacts);

            // Collect file metadata for changed documents in this project
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null) continue;
                if (!changedAbsolute.Contains(NormalizePath(doc.FilePath))) continue;

                try
                {
                    string content = await File.ReadAllTextAsync(doc.FilePath, ct).ConfigureAwait(false);
                    string sha256 = ComputeSha256(content);
                    string normalizedPath = doc.FilePath.Replace('\\', '/');

                    FilePath relPath;
                    if (normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                        relPath = FilePath.From(normalizedPath[normalizedDir.Length..]);
                    else
                        relPath = FilePath.From(Path.GetFileName(normalizedPath));

                    newFiles.Add(new ExtractedFile(
                        FileId: sha256[..16],
                        Path: relPath,
                        Sha256Hash: sha256,
                        ProjectName: project.Name));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read changed file {Path}", doc.FilePath);
                }
            }
        }

        // Use changedFiles (repo-relative) for the diff — these are the authoritative paths
        var delta = await _differ.ComputeDeltaAsync(
            baselineStore, repoId, commitSha,
            changedFiles, newSymbols, newRefs, newFiles,
            currentRevision, ct);

        // Attach extracted facts (not part of SymbolDiffer — additive)
        return newFacts.Count > 0 ? delta with { Facts = newFacts } : delta;
    }

    /// <summary>
    /// Builds a path → DocumentId index over the entire solution in O(D) time,
    /// so the warm-path can look up changed documents in O(1) rather than O(N×D).
    /// </summary>
    private static Dictionary<string, DocumentId> BuildDocumentPathIndex(Solution solution)
    {
        var index = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
            foreach (var document in project.Documents)
                if (document.FilePath is not null)
                    index.TryAdd(NormalizePath(document.FilePath), document.Id);
        return index;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static string ComputeSha256(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Returns the <see cref="Compilation"/> from the first successfully compiled
    /// project in the cached solution, or <c>null</c> if no solution has been
    /// loaded yet. Used by <see cref="MetadataResolver"/> to access DLL metadata
    /// without triggering a fresh MSBuildWorkspace open.
    /// </summary>
    internal async Task<Compilation?> GetMetadataCompilationAsync(CancellationToken ct = default)
    {
        var solution = _cachedSolution;
        if (solution is null) return null;

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is not null) return compilation;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetCompilationAsync failed for project {Project} — trying next", project.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// Releases the <c>SemaphoreSlim</c> and disposes the cached <c>MSBuildWorkspace</c>.
    /// Must be called when the singleton is torn down (daemon shutdown).
    /// </summary>
    public void Dispose()
    {
        _lock.Dispose();
        _cachedWorkspace?.Dispose();
        _cachedWorkspace = null;
        _cachedSolution = null;
    }
}
