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

        // Accept either a solution file (.sln/.slnx) or a bare project file
        // (.csproj/.vbproj/.fsproj). The .csproj fallback (M19 PHASE-19-01-T04)
        // unblocks `dotnet new` template scaffolds that ship without a solution.
        var swEval = Stopwatch.StartNew();
        Solution solution;
        if (IsProjectFile(solutionPath))
        {
            _logger.LogInformation("Opening project (solution-less): {ProjectPath}", solutionPath);
            var project = await workspace.OpenProjectAsync(solutionPath, cancellationToken: ct);
            solution = project.Solution;
        }
        else
        {
            _logger.LogInformation("Opening solution: {SolutionPath}", solutionPath);
            solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        }
        swEval.Stop();
        var totalProjectsLoaded = solution.Projects.Count();
        _logger.LogInformation(
            "PHASE_TIMING msbuild_eval_ms={Ms} total_projects_loaded={Count} solution={Path}",
            swEval.ElapsedMilliseconds, totalProjectsLoaded, solutionPath);

        // M19 PHASE-19-01-T06: If a project has <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>,
        // the SDK also adds the persisted Razor SG output (Generated/Microsoft.CodeAnalysis.Razor.Compiler/...)
        // to the project as <Compile> items. The Razor SG re-runs at compilation time
        // and emits the same partial types in-memory, producing duplicate-symbol compile
        // errors. Strip the on-disk copies so the in-memory SG output is the single source.
        solution = StripPersistedRazorSgFiles(solution);

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
    // CanonicalName / TargetFrameworks reflect the M20-01 multi-target collapse:
    // Project is the winning TFM (typically highest), CanonicalName is the
    // TFM-stripped name (e.g. "MudBlazor"), and TargetFrameworks lists every
    // TFM the .csproj declared (null when single-target).
    internal sealed record ProjectPassData(
        Project Project,
        string CanonicalName,
        IReadOnlyList<string>? TargetFrameworks,
        Compilation Compilation,
        IReadOnlyList<SymbolCard> Symbols,
        IReadOnlyDictionary<string, StableId> StableIdMap,
        IReadOnlyList<DiagnosticSeverity> ErrorSeverities,
        IReadOnlyList<string> ErrorMessages);

    private sealed record FSharpPassData(
        string ProjectName,
        IReadOnlyList<FSharp.FSharpFileAnalysis> Analyses,
        IReadOnlyDictionary<string, StableId> StableIdMap);

    internal sealed record Pass2Result(
        IReadOnlyList<ExtractedReference> References,
        IReadOnlyList<ExtractedFact> Facts,
        Core.Models.ProjectDiagnostic Diagnostic,
        long RefsMs,
        long FactsMs);

    /// <summary>
    /// Runs Pass-2 (refs + facts) for a single project group. Pure with respect
    /// to <see cref="ProjectPassData"/> input — no shared mutable state — so
    /// the caller can invoke this concurrently across <c>passData</c> entries.
    /// </summary>
    internal Pass2Result ExecutePass2Project(
        ProjectPassData pd, string solutionDir, IReadOnlySet<string> allSymbolIds)
    {
        var swRefs = Stopwatch.StartNew();
        var references = ReferenceExtractor.ExtractAll(
            pd.Compilation, solutionDir, pd.StableIdMap, allSymbolIds, _logger);
        swRefs.Stop();

        // Skip architectural fact extraction for test/benchmark projects.
        // Use canonical name (TFM stripped) so multi-target test projects
        // like "MyLib.Tests(net8.0)" still match the suffix predicate.
        bool isTestProject = pd.CanonicalName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || pd.CanonicalName.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)
            || pd.CanonicalName.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase);

        var facts = new List<ExtractedFact>();
        var swFacts = Stopwatch.StartNew();
        if (!isTestProject)
        {
            if (pd.Project.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            {
                facts.AddRange(Extraction.VbNet.VbEndpointExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbConfigKeyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbDbTableExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbDiRegistrationExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbMiddlewareExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbRetryPolicyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbExceptionExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(Extraction.VbNet.VbLogExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
            }
            else
            {
                facts.AddRange(EndpointExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(ConfigKeyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(DbTableExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(DiRegistrationExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(MiddlewareExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(RetryPolicyExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(ExceptionExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(LogExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
                facts.AddRange(RazorComponentExtractor.ExtractAll(pd.Compilation, solutionDir, pd.StableIdMap));
            }
        }
        swFacts.Stop();

        _logger.LogInformation(
            "PHASE_TIMING_PASS2 project={Project} refs_ms={RefsMs} facts_ms={FactsMs} ref_count={RefCount}",
            pd.CanonicalName, swRefs.ElapsedMilliseconds, swFacts.ElapsedMilliseconds, references.Count);

        var diagnostic = new Core.Models.ProjectDiagnostic(
            ProjectName: pd.CanonicalName,
            Compiled: true,
            SymbolCount: pd.Symbols.Count,
            ReferenceCount: references.Count,
            Errors: pd.ErrorMessages.Count > 0 ? pd.ErrorMessages : null,
            TargetFrameworks: pd.TargetFrameworks);

        return new Pass2Result(references, facts, diagnostic, swRefs.ElapsedMilliseconds, swFacts.ElapsedMilliseconds);
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
        var swPass0 = Stopwatch.StartNew();
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

        // M20-01: collapse multi-target compilations. solution.Projects returns
        // one Project per (csproj × TargetFramework) pair; we group by .csproj
        // FilePath and run extraction once per group on the canonical (highest)
        // TFM. This eliminates the 3×–7× duplication seen on Blazor component
        // libraries that target net8/9/10. See PHASE-20-01.md.
        swPass0.Stop();
        _logger.LogInformation(
            "PHASE_TIMING fsharp_pass_ms={Ms} fsproj_count={Count}",
            swPass0.ElapsedMilliseconds, fsprojPaths.Count);

        // ── Pass 1: per-csproj-group canonical compile + extract.
        var swPass1 = Stopwatch.StartNew();
        long totalBindMs = 0;
        long totalExtractMs = 0;
        int totalBindAttempts = 0;
        int totalGroups = 0;
        foreach (var group in RoslynProjectGrouping.GroupByFilePath(solution.Projects))
        {
            ct.ThrowIfCancellationRequested();
            totalGroups++;
            // Multi-target groups expose all TFMs; single-target keeps the
            // ProjectDiagnostic.TargetFrameworks field at null for wire stability.
            var tfmsForDiagnostic = group.AllProjects.Count > 1
                ? group.TargetFrameworks
                : null;

            // Try ranked candidates in order: canonical (highest TFM) first.
            // On null/exception, walk down. This survives the case where the
            // highest TFM has unique compile errors but a lower TFM compiles
            // cleanly (rare but seen on net10.0-preview repos).
            Project? winningProject = null;
            Compilation? winningCompilation = null;
            Exception? lastException = null;
            var swBind = Stopwatch.StartNew();
            int groupBindAttempts = 0;
            foreach (var candidate in group.AllProjects)
            {
                ct.ThrowIfCancellationRequested();
                groupBindAttempts++;
                try
                {
                    var c = await candidate.GetCompilationAsync(ct);
                    if (c is not null)
                    {
                        winningProject = candidate;
                        winningCompilation = c;
                        break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }
            swBind.Stop();
            totalBindMs += swBind.ElapsedMilliseconds;
            totalBindAttempts += groupBindAttempts;
            _logger.LogDebug(
                "PHASE_TIMING_GROUP project={Project} bind_ms={BindMs} bind_attempts={Attempts} tfm_count={TfmCount}",
                group.CanonicalName, swBind.ElapsedMilliseconds, groupBindAttempts, group.AllProjects.Count);

            if (winningProject is null || winningCompilation is null)
            {
                // Every TFM failed to produce a Compilation. Fall back syntactically.
                _logger.LogError(lastException, "Failed to compile any TFM for {Project}", group.CanonicalName);
                confidence = Confidence.Low;
                EmitSyntacticFallback(
                    group, lastException, allSymbols, allReferences, projectDiagnostics,
                    tfmsForDiagnostic, solutionDir);
                continue;
            }

            // Pass 1 success path. M19/M20-01 regression-guard: any throw from
            // GetDiagnostics / SymbolExtractor / ExtractFiles / TypeRelationExtractor
            // (e.g. NRE in a visitor on a malformed compilation, locked-file IO in
            // ExtractFiles, cycle in TypeRelationExtractor's base-type walk) used to
            // be caught alongside the GetCompilationAsync failure. After the M20-01
            // grouping refactor, that catch only wrapped GetCompilationAsync. Restore
            // the broader guard so a single bad project can't kill the whole index.
            var swExtract = Stopwatch.StartNew();
            try
            {
                firstCompilation ??= winningCompilation;

                var errors = winningCompilation.GetDiagnostics(ct)
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count > 0)
                {
                    _logger.LogWarning("Project {Project} has {Count} compilation error(s)",
                        group.CanonicalName, errors.Count);
                    if (confidence == Confidence.High)
                        confidence = Confidence.Medium;
                }

                var (symbols, stableIdMap) = SymbolExtractor.ExtractAllWithStableIds(
                    winningCompilation, group.CanonicalName, solutionDir);
                var files = ExtractFiles(winningProject, winningCompilation, group.CanonicalName, solutionDir);
                var typeRelations = TypeRelationExtractor.ExtractAll(winningCompilation, stableIdMap);

                allSymbols.AddRange(symbols);
                allFiles.AddRange(files);
                allTypeRelations.AddRange(typeRelations);

                passData.Add(new ProjectPassData(
                    Project: winningProject,
                    CanonicalName: group.CanonicalName,
                    TargetFrameworks: tfmsForDiagnostic,
                    Compilation: winningCompilation,
                    Symbols: symbols,
                    StableIdMap: stableIdMap,
                    ErrorSeverities: errors.Select(e => e.Severity).ToList(),
                    ErrorMessages: errors.Take(5).Select(e => e.GetMessage()).ToList()));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception extractionEx)
            {
                _logger.LogError(extractionEx,
                    "Extraction failed for {Project} after successful compilation — falling back syntactically",
                    group.CanonicalName);
                confidence = Confidence.Low;
                EmitSyntacticFallback(
                    group, extractionEx, allSymbols, allReferences, projectDiagnostics,
                    tfmsForDiagnostic, solutionDir);
            }
            swExtract.Stop();
            totalExtractMs += swExtract.ElapsedMilliseconds;
        }
        swPass1.Stop();
        _logger.LogInformation(
            "PHASE_TIMING pass1_total_ms={TotalMs} bind_sum_ms={BindMs} extract_sum_ms={ExtractMs} groups={Groups} bind_attempts={Attempts}",
            swPass1.ElapsedMilliseconds, totalBindMs, totalExtractMs, totalGroups, totalBindAttempts);

        // ── Pass 2: extract refs and facts now that all project symbols are known.
        // allSymbolIds covers every project so cross-language project refs resolve.
        var swPass2 = Stopwatch.StartNew();
        long totalRefsMs = 0;
        long totalFactsMs = 0;
        var allSymbolIds = allSymbols.Select(s => s.SymbolId.Value)
            .ToHashSet(StringComparer.Ordinal);

        // M20-02 Option A: parallelize Pass-2 across projects.
        // Each project's Compilation is independent and Roslyn SemanticModel
        // queries are documented thread-safe across distinct compilations.
        // Per-iteration buffers avoid contention; merge below preserves
        // passData order so projectDiagnostics ordering stays deterministic.
        var perProject = new Pass2Result[passData.Count];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };
        Parallel.For(0, passData.Count, parallelOptions, i =>
        {
            perProject[i] = ExecutePass2Project(passData[i], solutionDir, allSymbolIds);
        });

        for (int i = 0; i < passData.Count; i++)
        {
            var r = perProject[i];
            allReferences.AddRange(r.References);
            allFacts.AddRange(r.Facts);
            projectDiagnostics.Add(r.Diagnostic);
            totalRefsMs += r.RefsMs;
            totalFactsMs += r.FactsMs;
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
        swPass2.Stop();
        _logger.LogInformation(
            "PHASE_TIMING pass2_total_ms={TotalMs} refs_sum_ms={RefsMs} facts_sum_ms={FactsMs}",
            swPass2.ElapsedMilliseconds, totalRefsMs, totalFactsMs);

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

    /// <summary>
    /// Shared syntactic-fallback path used when (a) every TFM in a multi-target
    /// group fails to produce a Compilation, or (b) the success-path extractors
    /// throw on a successfully-compiled project. Emits one ProjectDiagnostic for
    /// the group with <see cref="Confidence.Low"/> semantics and the canonical
    /// name + target frameworks for downstream display.
    /// </summary>
    private void EmitSyntacticFallback(
        RoslynProjectGrouping.ProjectGroup group,
        Exception? failureCause,
        List<SymbolCard> allSymbols,
        List<ExtractedReference> allReferences,
        List<Core.Models.ProjectDiagnostic> projectDiagnostics,
        IReadOnlyList<string>? tfmsForDiagnostic,
        string solutionDir)
    {
        var fallbackProject = group.AllProjects[0];
        try
        {
            if (fallbackProject.Language == Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
            {
                _logger.LogInformation("VB.NET syntactic fallback for {Project}", group.CanonicalName);
                var (vbSymbols, vbRefs) = Extraction.VbNet.VbSyntacticExtractor.ExtractAll(fallbackProject, solutionDir);
                allSymbols.AddRange(vbSymbols);
                allReferences.AddRange(vbRefs);
                projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                    ProjectName: group.CanonicalName,
                    Compiled: false,
                    SymbolCount: vbSymbols.Count,
                    ReferenceCount: vbRefs.Count,
                    Errors: [failureCause?.Message ?? "Compilation returned null"],
                    TargetFrameworks: tfmsForDiagnostic));
                return;
            }

            var fallbackFiles = GetProjectSourceFiles(fallbackProject).ToList();
            var fallbackSymbols = SyntacticFallback.Extract(fallbackFiles);
            var fallbackRefs = SyntacticReferenceExtractor.ExtractAll(fallbackFiles, solutionDir);
            allSymbols.AddRange(fallbackSymbols);
            allReferences.AddRange(fallbackRefs);

            projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                ProjectName: group.CanonicalName,
                Compiled: false,
                SymbolCount: fallbackSymbols.Count,
                ReferenceCount: fallbackRefs.Count,
                Errors: [failureCause?.Message ?? "Compilation returned null"],
                TargetFrameworks: tfmsForDiagnostic));
        }
        catch (Exception fallbackEx)
        {
            _logger.LogError(fallbackEx,
                "Syntactic fallback also failed for {Project} — skipping", group.CanonicalName);
            projectDiagnostics.Add(new Core.Models.ProjectDiagnostic(
                ProjectName: group.CanonicalName,
                Compiled: false,
                SymbolCount: 0,
                ReferenceCount: 0,
                Errors: [
                    failureCause?.Message ?? "Compilation returned null",
                    $"Syntactic fallback also failed: {fallbackEx.Message}"
                ],
                TargetFrameworks: tfmsForDiagnostic));
        }
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

    /// <summary>
    /// Emits <see cref="ExtractedFile"/> records for every source file the
    /// compilation sees, plus synthetic entries for Razor SG output and the
    /// originating <c>.razor</c> files referenced via <c>#pragma checksum</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="canonicalProjectName"/> is the TFM-stripped name from
    /// <see cref="RoslynProjectGrouping.StripTfm"/>. Using <c>project.Name</c>
    /// here would persist the TFM suffix into <see cref="ExtractedFile.ProjectName"/>,
    /// which then flows into <c>EngineBaselineBuilder</c>'s <c>isTest</c> bit
    /// (<c>EndsWith(".Tests")</c> would miss <c>"MyLib.Tests(net10.0)"</c>) AND
    /// into <c>RecordMappers.ComputeDegradedStableId</c>'s SHA hash — meaning
    /// stable IDs would change every time the canonical TFM rotates (e.g. user
    /// adds <c>net11.0</c> to <c>&lt;TargetFrameworks&gt;</c>). Both regressions
    /// are avoided by using the canonical name throughout.
    /// </remarks>
    private static IReadOnlyList<ExtractedFile> ExtractFiles(
        Project project, Compilation? compilation, string canonicalProjectName, string solutionDir)
    {
        var files = new List<ExtractedFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var doc in project.Documents)
        {
            if (doc.FilePath is null) continue;

            try
            {
                string content = File.ReadAllText(doc.FilePath);
                AddFile(doc.FilePath, content, canonicalProjectName, normalizedDir, files, seenPaths);
            }
            catch (Exception)
            {
                // Skip unreadable files (permissions, locks, etc.)
            }
        }

        // Source-generator output (Razor backing classes, etc.) lives in
        // compilation.SyntaxTrees but NOT in project.Documents. Without a file
        // entry the baseline builder filters out their symbols. Emit a synthetic
        // ExtractedFile per SG tree so symbols on that path survive persistence.
        if (compilation is not null)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (string.IsNullOrEmpty(tree.FilePath)) continue;
                var normalized = tree.FilePath.Replace('\\', '/');
                if (seenPaths.Contains(normalized)) continue;
                if (seenPaths.Contains(NormalizeForSeen(tree.FilePath, normalizedDir))) continue;
                try
                {
                    var content = tree.GetText().ToString();
                    AddFile(tree.FilePath, content, canonicalProjectName, normalizedDir, files, seenPaths);

                    // Razor SG output starts with `#pragma checksum "<original .razor>"`.
                    // Emit a synthetic ExtractedFile for the original so the baseline
                    // builder accepts facts whose FilePath has been remapped to the
                    // user-authored path (M19 EndpointExtractor / RazorComponentExtractor).
                    //
                    // SECURITY: The pragma path is taken verbatim from arbitrary user
                    // source files. Bound the read to paths under solutionDir so a
                    // malicious or hand-crafted #pragma checksum can't trick CodeMap
                    // into indexing files outside the repo (e.g. /etc/passwd, secrets).
                    var razorOriginal = Extraction.Razor.RazorSgHelpers.ParseChecksumPath(content);
                    if (razorOriginal is not null
                        && IsUnderSolutionDir(razorOriginal, solutionDir)
                        && File.Exists(razorOriginal))
                    {
                        try
                        {
                            var razorContent = File.ReadAllText(razorOriginal);
                            AddFile(razorOriginal, razorContent, canonicalProjectName, normalizedDir, files, seenPaths);
                        }
                        catch
                        {
                            // Original .razor unreadable — skip; fact will be dropped.
                        }
                    }
                }
                catch
                {
                    // Skip if tree text is unavailable.
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> resolves to a path inside
    /// <paramref name="solutionDir"/>. Used to bound the file-read in <see cref="ExtractFiles"/>
    /// against #pragma checksum directives from untrusted source files.
    /// </summary>
    internal static bool IsUnderSolutionDir(string candidate, string solutionDir)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(solutionDir))
            return false;
        try
        {
            var candidateFull = Path.GetFullPath(candidate).Replace('\\', '/').TrimEnd('/');
            var rootFull = Path.GetFullPath(solutionDir).Replace('\\', '/').TrimEnd('/') + '/';
            return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Path.GetFullPath throws on invalid path chars, NotSupportedException, etc.
            return false;
        }
    }

private static void AddFile(
        string filePath, string content, string projectName, string normalizedDir,
        List<ExtractedFile> files, HashSet<string> seenPaths)
    {
        string normalizedPath = filePath.Replace('\\', '/');

        FilePath relativePath;
        if (normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            relativePath = FilePath.From(normalizedPath[normalizedDir.Length..]);
        else
            relativePath = FilePath.From(Path.GetFileName(normalizedPath));

        if (!seenPaths.Add(relativePath.Value)) return;
        seenPaths.Add(normalizedPath);

        string sha256 = ComputeSha256(content);
        string fileId = sha256[..16];

        files.Add(new ExtractedFile(
            FileId: fileId,
            Path: relativePath,
            Sha256Hash: sha256,
            ProjectName: projectName,
            Content: content));
    }

    private static string NormalizeForSeen(string filePath, string normalizedDir)
    {
        var normalized = filePath.Replace('\\', '/');
        return normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)
            ? normalized[normalizedDir.Length..]
            : Path.GetFileName(normalized);
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

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes documents whose file path indicates persisted Razor SG output.
    /// The Razor source generator re-emits these types virtually during
    /// <c>GetCompilationAsync</c>, so keeping the on-disk copies produces
    /// duplicate-type errors and double-counted symbols.
    /// </summary>
    private static Solution StripPersistedRazorSgFiles(Solution solution)
    {
        var toRemove = new List<DocumentId>();
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (IsPersistedRazorSgPath(document.FilePath))
                    toRemove.Add(document.Id);
            }
        }
        foreach (var docId in toRemove)
            solution = solution.RemoveDocument(docId);
        return solution;
    }

    /// <summary>
    /// Returns true when <paramref name="filePath"/> looks like persisted Razor
    /// SG output — the SDK writes such files under
    /// <c>Generated/Microsoft.CodeAnalysis.Razor.Compiler/...</c> when a project
    /// sets <c>&lt;EmitCompilerGeneratedFiles&gt;true&lt;/EmitCompilerGeneratedFiles&gt;</c>.
    /// Cross-platform: paths are normalised to forward slashes before matching.
    /// </summary>
    internal static bool IsPersistedRazorSgPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        const string Marker = "/Generated/Microsoft.CodeAnalysis.Razor.Compiler/";
        var normalised = filePath.Replace('\\', '/');
        return normalised.Contains(Marker, StringComparison.Ordinal);
    }
}
