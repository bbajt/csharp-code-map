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

        workspace.RegisterWorkspaceFailedHandler((args) =>
            _logger.LogWarning("Workspace diagnostic [{Kind}]: {Message}",
                args.Diagnostic.Kind, args.Diagnostic.Message));

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

    // Holds per-project data between the symbol pass and the ref pass.
    private sealed record ProjectPassData(
        Project Project,
        Compilation Compilation,
        IReadOnlyList<SymbolCard> Symbols,
        IReadOnlyDictionary<string, StableId> StableIdMap,
        IReadOnlyList<DiagnosticSeverity> ErrorSeverities,
        IReadOnlyList<string> ErrorMessages);

    private sealed record FSharpPassData(
        string ProjectName,
        IReadOnlyList<FSharp.FSharpFileAnalysis> Analyses,
        IReadOnlyDictionary<string, StableId> StableIdMap);

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
        Compilation? firstCompilation = null;

        // ── Pass 1: compile every project, extract symbols/files/typeRelations.
        // Refs are deferred to Pass 2 so the complete cross-project symbol set is
        // available — required for cross-language (VB→C#, C#→VB) project refs where
        // Roslyn uses MetadataReference (no IsInSource locations) rather than
        // CompilationReference. solution.Projects order follows the .sln file, not
        // build-dependency order, so a streaming accumulation cannot work.
        var passData = new List<ProjectPassData>();
        var fsPassData = new List<FSharpPassData>();

        // ── Pass 0: F# projects (MSBuildWorkspace doesn't load them at all).
        // Scan the .sln for .fsproj entries and process via FCS bridge.
        var fsprojPaths = FindFSharpProjects(solution);
        foreach (var fsprojPath in fsprojPaths)
        {
            ct.ThrowIfCancellationRequested();
            var projectName = Path.GetFileNameWithoutExtension(fsprojPath);
            try
            {
                _logger.LogInformation("F# project detected: {Project}, using FCS", projectName);
                var fsAnalyses = FSharp.FSharpProjectAnalyzer.AnalyzeProject(fsprojPath, solutionDir, ct);
                var sourceFiles = fsAnalyses.Select(a => a.FilePath).ToList();
                var fsFiles = FSharp.FSharpFileExtractor.ExtractFiles(sourceFiles, projectName, solutionDir);
                var (fsSymbols, fsStableIdMap) = FSharp.FSharpSymbolMapper.ExtractSymbols(fsAnalyses, projectName, solutionDir);
                var fsTypeRelations = FSharp.FSharpTypeRelationMapper.ExtractTypeRelations(fsAnalyses, fsStableIdMap);

                allSymbols.AddRange(fsSymbols);
                allFiles.AddRange(fsFiles);
                allTypeRelations.AddRange(fsTypeRelations);

                bool allChecked = fsAnalyses.All(a => a.CheckResults != null);
                projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                    ProjectName: projectName,
                    Compiled: allChecked,
                    SymbolCount: fsSymbols.Count,
                    ReferenceCount: 0,
                    Errors: allChecked ? [] : ["Some F# files failed type-check"]));

                fsPassData.Add(new FSharpPassData(projectName, fsAnalyses, fsStableIdMap));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze F# project {Project}", projectName);
                try
                {
                    _logger.LogInformation("F# syntactic fallback for {Project}", projectName);
                    var (fallbackSymbols, fallbackRefs) =
                        FSharp.FSharpSyntacticFallback.ExtractAll(fsprojPath, solutionDir);
                    allSymbols.AddRange(fallbackSymbols);
                    allReferences.AddRange(fallbackRefs);
                    projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                        ProjectName: projectName, Compiled: false,
                        SymbolCount: fallbackSymbols.Count, ReferenceCount: fallbackRefs.Count,
                        Errors: [$"F# analysis failed (syntactic fallback): {ex.Message}"]));
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "F# syntactic fallback also failed for {Project}", projectName);
                    projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                        ProjectName: projectName, Compiled: false,
                        SymbolCount: 0, ReferenceCount: 0,
                        Errors: [ex.Message, $"Syntactic fallback also failed: {fallbackEx.Message}"]));
                }
                confidence = Confidence.Low;
            }
        }

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var compilation = await project.GetCompilationAsync(ct);
                firstCompilation ??= compilation;
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
                var files = ExtractFiles(project, solutionDir);
                var typeRelations = TypeRelationExtractor.ExtractAll(compilation, stableIdMap);

                allSymbols.AddRange(symbols);
                allFiles.AddRange(files);
                allTypeRelations.AddRange(typeRelations);

                passData.Add(new ProjectPassData(
                    project, compilation, symbols, stableIdMap,
                    errors.Select(e => e.Severity).ToList(),
                    errors.Take(5).Select(e => e.GetMessage()).ToList()));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compile project {Project}", project.Name);
                confidence = Confidence.Low;

                // Syntactic fallback — guarded separately because project.Documents may also
                // be unavailable when the project itself failed to load (e.g. missing .NET
                // Framework targeting pack). Without this inner guard the NRE would propagate
                // out of the catch block and crash the entire indexing run.
                try
                {
                    // VB.NET syntactic fallback — syntax-level symbols and unresolved edges.
                    if (project.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
                    {
                        _logger.LogInformation("VB.NET syntactic fallback for {Project}", project.Name);
                        var (vbSymbols, vbRefs) = Extraction.VbNet.VbSyntacticExtractor.ExtractAll(project, solutionDir);
                        allSymbols.AddRange(vbSymbols);
                        allReferences.AddRange(vbRefs);
                        projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                            ProjectName: project.Name,
                            Compiled: false,
                            SymbolCount: vbSymbols.Count,
                            ReferenceCount: vbRefs.Count,
                            Errors: [ex.Message]));
                        continue;
                    }

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
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx,
                        "Syntactic fallback also failed for {Project} — skipping", project.Name);
                    projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                        ProjectName: project.Name,
                        Compiled: false,
                        SymbolCount: 0,
                        ReferenceCount: 0,
                        Errors: [ex.Message, $"Syntactic fallback also failed: {fallbackEx.Message}"]));
                }
            }
        }

        // ── Pass 2: extract refs and facts now that all project symbols are known.
        // allSymbolIds covers every project so cross-language project refs resolve.
        var allSymbolIds = allSymbols.Select(s => s.SymbolId.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pd in passData)
        {
            ct.ThrowIfCancellationRequested();

            var references = ReferenceExtractor.ExtractAll(
                pd.Compilation, solutionDir, pd.StableIdMap, allSymbolIds);
            allReferences.AddRange(references);

            // Skip architectural fact extraction for test/benchmark projects.
            bool isTestProject = pd.Project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || pd.Project.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)
                || pd.Project.Name.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase);

            if (!isTestProject)
            {
                if (pd.Project.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
                {
                    allFacts.AddRange(Extraction.VbNet.VbEndpointExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbConfigKeyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbDbTableExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbDiRegistrationExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbMiddlewareExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbRetryPolicyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbExceptionExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(Extraction.VbNet.VbLogExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                }
                else
                {
                    allFacts.AddRange(EndpointExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(ConfigKeyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(DbTableExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(DiRegistrationExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(MiddlewareExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(RetryPolicyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(ExceptionExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                    allFacts.AddRange(LogExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                }
            }

            projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                ProjectName: pd.Project.Name,
                Compiled: true,
                SymbolCount: pd.Symbols.Count,
                ReferenceCount: references.Count,
                Errors: pd.ErrorMessages.Count > 0 ? pd.ErrorMessages : null));
        }

        // ── Pass 2b: extract F# references now that allSymbolIds is complete.
        foreach (var fsPd in fsPassData)
        {
            ct.ThrowIfCancellationRequested();
            var fsRefs = FSharp.FSharpReferenceMapper.ExtractReferences(
                fsPd.Analyses, solutionDir, fsPd.StableIdMap, allSymbolIds);
            allReferences.AddRange(fsRefs);

            // Update the diagnostic with the ref count (was 0 in Pass 1)
            var existingDiag = projectDiagnostics.FindIndex(d => d.ProjectName == fsPd.ProjectName);
            if (existingDiag >= 0)
            {
                var old = projectDiagnostics[existingDiag];
                projectDiagnostics[existingDiag] = old with { ReferenceCount = fsRefs.Count };
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

        string? dllFingerprint = firstCompilation is not null
            ? BuildDllFingerprint(firstCompilation)
            : null;

        return new CompilationResult(allSymbols, allReferences, allFiles, stats, allTypeRelations, allFacts,
            DllFingerprint: dllFingerprint);
    }

    private static string? BuildDllFingerprint(Compilation compilation)
    {
        var fingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in compilation.References.OfType<Microsoft.CodeAnalysis.PortableExecutableReference>())
        {
            if (reference.FilePath is null || !File.Exists(reference.FilePath))
                continue;
            try
            {
                byte[] bytes = File.ReadAllBytes(reference.FilePath);
                byte[] hash = SHA256.HashData(bytes);
                string name = Path.GetFileNameWithoutExtension(reference.FilePath);
                fingerprint.TryAdd(name, Convert.ToHexStringLower(hash));
            }
            catch
            {
                // Skip unreadable references
            }
        }
        if (fingerprint.Count == 0) return null;
        return System.Text.Json.JsonSerializer.Serialize(fingerprint);
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
                    ProjectName: project.Name,
                    Content: content));
            }
            catch (Exception)
            {
                // Skip unreadable files (permissions, locks, etc.)
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

    /// <summary>
    /// Scans the .sln file for .fsproj entries. MSBuildWorkspace doesn't load F# projects,
    /// so we detect them from the solution file directly and process via FCS.
    /// </summary>
    private static IReadOnlyList<string> FindFSharpProjects(Solution solution)
    {
        var solutionPath = solution.FilePath;
        if (string.IsNullOrEmpty(solutionPath)) return [];

        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var fsprojPaths = new List<string>();

        try
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                // .slnx format: XML with <Project Path="relative/path.fsproj" />
                var xml = System.Xml.Linq.XDocument.Load(solutionPath);
                foreach (var proj in xml.Descendants().Where(e => e.Name.LocalName == "Project"))
                {
                    var pathAttr = proj.Attribute("Path")?.Value;
                    if (pathAttr != null && pathAttr.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(solutionDir, pathAttr.Replace('\\', Path.DirectorySeparatorChar)));
                        if (File.Exists(fullPath))
                            fsprojPaths.Add(fullPath);
                    }
                }
            }
            else
            {
                // .sln format: Project("{...}") = "Name", "relative\path.fsproj", "{...}"
                var lines = File.ReadAllLines(solutionPath);
                foreach (var line in lines)
                {
                    if (!line.Contains(".fsproj", StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = line.Split('"');
                    foreach (var part in parts)
                    {
                        if (part.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                        {
                            var fullPath = Path.GetFullPath(Path.Combine(solutionDir, part.Replace('\\', Path.DirectorySeparatorChar)));
                            if (File.Exists(fullPath))
                                fsprojPaths.Add(fullPath);
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // If we can't read the solution file, return empty — F# projects just won't be indexed
        }

        return fsprojPaths;
    }
}
