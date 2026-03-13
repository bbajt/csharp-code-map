namespace CodeMap.Roslyn;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads a .NET solution via MSBuildWorkspace, compiles all projects,
/// and extracts symbols, references, and file metadata.
/// </summary>
public sealed class RoslynCompiler : IRoslynCompiler
{
    private readonly ILogger<RoslynCompiler> _logger;

    public RoslynCompiler(ILogger<RoslynCompiler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CompilationResult> CompileAndExtractAsync(
        string solutionPath, CancellationToken ct = default)
    {
        MsBuildInitializer.EnsureRegistered();

        if (!File.Exists(solutionPath))
            throw new FileNotFoundException($"Solution file not found: {solutionPath}", solutionPath);

        string solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var sw = Stopwatch.StartNew();

        using var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, args) =>
            _logger.LogWarning("Workspace diagnostic [{Kind}]: {Message}",
                args.Diagnostic.Kind, args.Diagnostic.Message);

        _logger.LogInformation("Opening solution: {SolutionPath}", solutionPath);
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        var result = await ExtractSolutionAsync(solution, solutionDir, ct);
        sw.Stop();

        var stats = result.Stats with { ElapsedSeconds = sw.Elapsed.TotalSeconds };
        return result with { Stats = stats };
    }

    /// <inheritdoc/>
    public async Task<CompilationResult> IncrementalExtractAsync(
        string solutionPath,
        IReadOnlyList<FilePath> changedFiles,
        CancellationToken ct = default)
    {
        // Milestone 01 simplified: full compile, filter to changed files
        var full = await CompileAndExtractAsync(solutionPath, ct);

        if (changedFiles.Count == 0)
        {
            return new CompilationResult([], [], [],
                new IndexStats(0, 0, 0, full.Stats.ElapsedSeconds, full.Stats.Confidence,
                    full.Stats.SemanticLevel));
        }

        var changedSet = changedFiles.Select(f => f.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filteredSymbols = full.Symbols
            .Where(s => changedSet.Contains(s.FilePath.Value))
            .ToList();

        var filteredRefs = full.References
            .Where(r => changedSet.Contains(r.FilePath.Value))
            .ToList();

        var filteredFiles = full.Files
            .Where(f => changedSet.Contains(f.Path.Value))
            .ToList();

        var stats = new IndexStats(
            SymbolCount: filteredSymbols.Count,
            ReferenceCount: filteredRefs.Count,
            FileCount: filteredFiles.Count,
            ElapsedSeconds: full.Stats.ElapsedSeconds,
            Confidence: full.Stats.Confidence,
            SemanticLevel: full.Stats.SemanticLevel);

        var filteredTypeRelations = full.TypeRelations?
            .Where(r => changedSet.Contains(r.TypeSymbolId.Value) ||
                        filteredSymbols.Any(s => s.SymbolId == r.TypeSymbolId))
            .ToList();

        var filteredFacts = full.Facts?
            .Where(f => changedSet.Contains(f.FilePath.Value))
            .ToList();

        return new CompilationResult(filteredSymbols, filteredRefs, filteredFiles, stats,
            filteredTypeRelations, filteredFacts);
    }

    private async Task<CompilationResult> ExtractSolutionAsync(
        Solution solution, string solutionDir, CancellationToken ct)
    {
        var allSymbols = new List<SymbolCard>();
        var allReferences = new List<ExtractedReference>();
        var allFiles = new List<ExtractedFile>();
        var allTypeRelations = new List<ExtractedTypeRelation>();
        var allFacts = new List<ExtractedFact>();
        var projectDiagnostics = new List<Core.Models.ProjectDiagnostic>();
        var confidence = Confidence.High;

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null)
                {
                    _logger.LogWarning("No compilation for project {Project}", project.Name);
                    projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                        ProjectName: project.Name,
                        Compiled: false,
                        SymbolCount: 0,
                        ReferenceCount: 0,
                        Errors: ["Compilation returned null"]));
                    confidence = Confidence.Low;
                    continue;
                }

                var errors = compilation.GetDiagnostics(ct)
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count > 0)
                {
                    _logger.LogWarning("Project {Project} has {Count} compilation error(s)",
                        project.Name, errors.Count);
                    if (confidence == Confidence.High)
                        confidence = Confidence.Medium;
                }

                var (symbols, stableIdMap) = SymbolExtractor.ExtractAllWithStableIds(compilation, project.Name, solutionDir);
                var references = ReferenceExtractor.ExtractAll(compilation, solutionDir, stableIdMap);
                var files = ExtractFiles(project, solutionDir);
                var typeRelations = TypeRelationExtractor.ExtractAll(compilation, stableIdMap);

                allSymbols.AddRange(symbols);
                allReferences.AddRange(references);
                allFiles.AddRange(files);
                allTypeRelations.AddRange(typeRelations);

                // Skip architectural fact extraction for test/benchmark projects.
                // Symbols and refs are still indexed (valid code), but facts like
                // exceptions thrown in assertions and log calls in test helpers
                // would pollute codemap.summarize and codemap.export.
                bool isTestProject = project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                    || project.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)
                    || project.Name.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase);

                if (!isTestProject)
                {
                    allFacts.AddRange(EndpointExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(ConfigKeyExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(DbTableExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(DiRegistrationExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(MiddlewareExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(RetryPolicyExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(ExceptionExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                    allFacts.AddRange(LogExtractor.ExtractAll(compilation, solutionDir, stableIdMap));
                }

                projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                    ProjectName: project.Name,
                    Compiled: true,
                    SymbolCount: symbols.Count,
                    ReferenceCount: references.Count,
                    Errors: errors.Count > 0
                        ? errors.Take(5).Select(e => e.GetMessage()).ToList()
                        : null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compile project {Project}", project.Name);
                confidence = Confidence.Low;

                var fallbackFiles = GetProjectSourceFiles(project).ToList();
                var fallbackSymbols = SyntacticFallback.Extract(fallbackFiles);
                var fallbackRefs = SyntacticReferenceExtractor.ExtractAll(fallbackFiles, solutionDir);
                allSymbols.AddRange(fallbackSymbols);
                allReferences.AddRange(fallbackRefs);

                projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                    ProjectName: project.Name,
                    Compiled: false,
                    SymbolCount: fallbackSymbols.Count,
                    ReferenceCount: fallbackRefs.Count,
                    Errors: [ex.Message]));
            }
        }

        // Compute SemanticLevel from per-project outcomes
        int compiledCount = projectDiagnostics.Count(d => d.Compiled);
        int totalCount = projectDiagnostics.Count;
        var semanticLevel = (compiledCount, totalCount) switch
        {
            (var c, var t) when c == t => Core.Enums.SemanticLevel.Full,
            (0, _) => Core.Enums.SemanticLevel.SyntaxOnly,
            _ => Core.Enums.SemanticLevel.Partial
        };

        var stats = new IndexStats(
            SymbolCount: allSymbols.Count,
            ReferenceCount: allReferences.Count,
            FileCount: allFiles.Count,
            ElapsedSeconds: 0, // set by caller after stopwatch
            Confidence: confidence,
            SemanticLevel: semanticLevel,
            ProjectDiagnostics: projectDiagnostics);

        return new CompilationResult(allSymbols, allReferences, allFiles, stats, allTypeRelations, allFacts);
    }

    private static IReadOnlyList<ExtractedFile> ExtractFiles(Project project, string solutionDir)
    {
        var files = new List<ExtractedFile>();
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var doc in project.Documents)
        {
            if (doc.FilePath is null) continue;

            try
            {
                string content = File.ReadAllText(doc.FilePath);
                string normalizedPath = doc.FilePath.Replace('\\', '/');

                FilePath relativePath;
                if (normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                    relativePath = FilePath.From(normalizedPath[normalizedDir.Length..]);
                else
                    relativePath = FilePath.From(Path.GetFileName(normalizedPath));

                string sha256 = ComputeSha256(content);
                string fileId = sha256[..16];

                files.Add(new ExtractedFile(
                    FileId: fileId,
                    Path: relativePath,
                    Sha256Hash: sha256,
                    ProjectName: project.Name));
            }
            catch (Exception)
            {
                // Skip unreadable files
            }
        }

        return files;
    }

    private static IEnumerable<(string FilePath, string Content)> GetProjectSourceFiles(Project project)
    {
        foreach (var doc in project.Documents)
        {
            if (doc.FilePath is null) continue;
            string content;
            try { content = File.ReadAllText(doc.FilePath); }
            catch { continue; }
            yield return (doc.FilePath, content);
        }
    }

    private static string ComputeSha256(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
