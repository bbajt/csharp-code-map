namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Serialization;
using CodeMap.Query;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>index_ensure_baseline</c>, <c>index_list_baselines</c> MCP tools.
/// Checks if a baseline index exists and builds one if not; lists cached baselines.
///
/// Cache flow for ensure_baseline:
///   1. Check local → return if exists
///   2. Check shared cache → pull and return if found
///   3. Build via Roslyn (existing path)
///   4. Push to shared cache after successful build
///
/// Per ADR-012, this handler calls <see cref="ISymbolStore.CreateBaselineAsync"/>
/// with <c>repoRootPath</c> so that <c>GetFileSpanAsync</c> can read from disk.
/// </summary>
/// <remarks>
/// <b>JSON params (ensure_baseline):</b> repo_path (required), solution_path (required), commit_sha (optional).
/// solution_path must be an absolute path to an existing .sln file.
/// commit_sha defaults to HEAD when omitted.
/// Returns COMPILATION_FAILED if the solution doesn't exist or compilation produces no symbols.
/// Response includes CommitSha, AlreadyExisted, Stats (nullable), and FromCache flag.
/// <br/>
/// <b>JSON params (list_baselines):</b> repo_path (required).
/// </remarks>
public sealed class IndexHandler
{
    private readonly IGitService _git;
    private readonly IRoslynCompiler _compiler;
    private readonly ISymbolStore _store;
    private readonly IBaselineCacheManager _cache;
    private readonly IBaselineScanner? _scanner;
    private readonly WorkspaceManager? _workspaceManager;
    private readonly IWorkspaceStickyRegistry? _stickyRegistry;
    private readonly IRepoRegistry _repoRegistry;
    private readonly ILogger<IndexHandler> _logger;
    private readonly IActivityMonitor? _activity;

    public IndexHandler(
        IGitService git,
        IRoslynCompiler compiler,
        ISymbolStore store,
        IBaselineCacheManager cache,
        IRepoRegistry repoRegistry,
        ILogger<IndexHandler> logger,
        IBaselineScanner? scanner = null,
        WorkspaceManager? workspaceManager = null,
        IWorkspaceStickyRegistry? stickyRegistry = null,
        IActivityMonitor? activity = null)
    {
        _activity = activity;
        _stickyRegistry = stickyRegistry;
        _git = git;
        _compiler = compiler;
        _store = store;
        _cache = cache;
        _repoRegistry = repoRegistry;
        _scanner = scanner;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Registers the <c>index_list_baselines</c>, <c>index_cleanup</c>,
    /// and <c>index_ensure_baseline</c> tools into the ToolRegistry.
    /// </summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            ToolNames.IndexListBaselines,
            "List all cached baselines for a repository, showing commit SHA, creation date, file size, and whether each is the current HEAD or referenced by an active workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root" },
                },
            },
            HandleListBaselinesAsync,
            HandlerHelpers.AnnotReadOnly));

        registry.Register(new ToolDefinition(
            ToolNames.IndexCleanup,
            "Remove old cached baselines to reclaim disk space. Never deleted: the current HEAD, and the baseline of any workspace of any CodeMap process. A baseline another process has open is left intact (skipped_in_use). repo_path is required. Default is dry_run:true — set dry_run:false to actually delete.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("repo_path"),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root (required: this tool deletes data)" },
                    ["keep_count"] = new JsonObject { ["type"] = "integer", ["description"] = "Keep the N most recent baselines (default: 5)" },
                    ["older_than_days"] = new JsonObject { ["type"] = "integer", ["description"] = "Remove baselines older than N days" },
                    ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "If true, report what would be deleted without deleting (default: true)" },
                },
            },
            HandleCleanupAsync,
            HandlerHelpers.AnnotDestructIdempotent));

        registry.Register(new ToolDefinition(
            ToolNames.IndexRemoveRepo,
            "Remove ALL cached baselines and workspace overlays of a repository, freeing all disk space. Unlike index_cleanup, this ignores protection rules — HEAD and workspace-referenced baselines are also deleted. Refuses (WORKSPACE_IN_USE, nothing deleted) while another CodeMap process has a workspace open in the repo; a baseline another process has open is left intact and listed in skipped_in_use. repo_path is required. Default is dry_run:true.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("repo_path"),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root (required: this tool deletes data)" },
                    ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "If true, report what would be deleted without deleting (default: true)" },
                },
            },
            HandleRemoveRepoAsync,
            HandlerHelpers.AnnotDestructIdempotent));

        registry.Register(new ToolDefinition(
            ToolNames.IndexEnsureBaseline,
            "Build a semantic index for a .NET solution. Idempotent: returns immediately if the current commit is already indexed.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(
                    (JsonNode?)"repo_path"),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root" },
                    ["solution_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to a .sln/.slnx solution OR a .csproj/.vbproj/.fsproj project file. Auto-discovered if omitted: .slnx > .sln > single project at repo root or in a single direct child directory." },
                    ["commit_sha"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: specific commit to index (default: HEAD). Accepts short SHAs." },
                },
            },
            HandleAsync,
            HandlerHelpers.AnnotWriteIdempotent));
    }

    internal async Task<ToolCallResult> HandleListBaselinesAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;
        if (_scanner is null) return ScannerNotConfigured(ToolNames.IndexListBaselines);

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);

            CommitSha? currentHead = null;
            try
            {
                currentHead = await _git.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "index_list_baselines: could not resolve HEAD for {RepoPath}", repoPath);
            }

            var baselines = await _scanner.ListBaselinesAsync(repoId, ct).ConfigureAwait(false);

            // Enrich with HEAD and workspace cross-references
            IReadOnlyList<CodeMap.Query.WorkspaceSummary> workspaces = _workspaceManager is not null
                ? await _workspaceManager.ListWorkspacesAsync(repoId, ct).ConfigureAwait(false)
                : [];
            var workspaceBaseShas = workspaces
                .Select(w => w.BaseCommitSha.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var enriched = baselines.Select(b => b with
            {
                IsCurrentHead = currentHead is not null &&
                    string.Equals(b.CommitSha.Value, currentHead.Value.Value, StringComparison.OrdinalIgnoreCase),
                IsActiveWorkspaceBase = workspaceBaseShas.Contains(b.CommitSha.Value),
            }).ToList();

            var response = new ListBaselinesResponse(
                RepoId: repoId,
                CurrentHead: currentHead,
                Baselines: enriched,
                TotalSizeBytes: enriched.Sum(b => b.SizeBytes));

            return new ToolCallResult(JsonSerializer.Serialize(response, CodeMapJsonOptions.Default));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index_list_baselines failed for {RepoPath}", repoPath);
            return Classified(ex, ToolNames.IndexListBaselines);
        }
    }

    internal async Task<ToolCallResult> HandleCleanupAsync(JsonObject? args, CancellationToken ct)
    {
        // Destructive: no auto-default to the sticky / only registered repo (PHASE-21-10).
        var (repoPath, repoErr) = RequireExplicitRepoPath(args, ToolNames.IndexCleanup);
        if (repoErr is { } re) return re;
        if (_scanner is null) return ScannerNotConfigured(ToolNames.IndexCleanup);

        var keepCount = args.GetInt("keep_count", 5);
        var olderThanDays = args.GetInt("older_than_days");
        var dryRun = args.GetBool("dry_run", true);

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
            var currentHead = await _git.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);

            IReadOnlyList<CodeMap.Query.WorkspaceSummary> workspaces = _workspaceManager is not null
                ? await _workspaceManager.ListWorkspacesAsync(repoId, ct).ConfigureAwait(false)
                : [];
            var workspaceBaseCommits = workspaces
                .Select(w => w.BaseCommitSha)
                .ToHashSet();

            var response = await _scanner.CleanupBaselinesAsync(
                repoId, currentHead, workspaceBaseCommits,
                keepCount, olderThanDays, dryRun, ct).ConfigureAwait(false);

            if (!response.DryRun && response.WorkspacesInUse is { Count: > 0 } inUse)
                return Err(new CodeMapError(
                    ErrorCodes.WorkspaceInUse,
                    $"index_cleanup refused: workspace(s) {string.Join(", ", inUse)} are open in another CodeMap " +
                    "process and don't record their baseline (created by a CodeMap older than v2.10.0), so any " +
                    "baseline could be theirs. Nothing was deleted. Retry after those sessions end.",
                    new Dictionary<string, object> { ["workspace_ids"] = inUse },
                    Retryable: true));

            var json = JsonSerializer.Serialize(response, CodeMapJsonOptions.Default);
            if (response.DryRun)
                json += "\n(Dry run — no files were actually deleted. Pass dry_run:false to delete.)";
            if (response.WorkspacesWithoutBaselineRecord is { Count: > 0 } unknown)
                json += $"\n(Workspaces {string.Join(", ", unknown)} don't record their baseline, so it can't be " +
                        "protected. If they are stale, remove them with workspace_delete.)";

            return new ToolCallResult(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index_cleanup failed for {RepoPath}", repoPath);
            return Classified(ex, ToolNames.IndexCleanup);
        }
    }

    internal async Task<ToolCallResult> HandleRemoveRepoAsync(JsonObject? args, CancellationToken ct)
    {
        // Destructive: no auto-default to the sticky / only registered repo (PHASE-21-10).
        var (repoPath, repoErr) = RequireExplicitRepoPath(args, ToolNames.IndexRemoveRepo);
        if (repoErr is { } re) return re;
        if (_scanner is null) return ScannerNotConfigured(ToolNames.IndexRemoveRepo);

        var dryRun = args.GetBool("dry_run", true);

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
            IReadOnlyList<CodeMap.Query.WorkspaceSummary> ownWorkspaces = !dryRun && _workspaceManager is not null
                ? await _workspaceManager.ListWorkspacesAsync(repoId, ct).ConfigureAwait(false)
                : [];

            var response = await _scanner.RemoveRepoAsync(repoId, dryRun, ct).ConfigureAwait(false);

            if (!dryRun && response.WorkspacesInUse is { Count: > 0 } inUse)
                return Err(new CodeMapError(
                    ErrorCodes.WorkspaceInUse,
                    $"index_remove_repo refused: workspace(s) {string.Join(", ", inUse)} are open in another CodeMap " +
                    "process. Nothing was deleted. Delete them there (workspace_delete) or stop those agents, then retry.",
                    new Dictionary<string, object> { ["workspace_ids"] = inUse },
                    Retryable: true));

            if (!dryRun)
            {
                // This process's workspaces were closed and their overlays removed with the repo; drop
                // them from the registry and the sticky default too.
                foreach (var ws in ownWorkspaces)
                {
                    await _workspaceManager!.DeleteWorkspaceAsync(repoId, ws.WorkspaceId, ct).ConfigureAwait(false);
                    _stickyRegistry?.Clear(repoPath!, ws.WorkspaceId.Value);
                }

                if (response.SkippedInUse is not { Count: > 0 })
                    _repoRegistry.Forget(repoPath!);
            }

            var json = JsonSerializer.Serialize(response, CodeMapJsonOptions.Default);
            if (response.DryRun)
                json += "\n(Dry run — no files were actually deleted. Pass dry_run:false to delete.)";

            return new ToolCallResult(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index_remove_repo failed for {RepoPath}", repoPath);
            return Classified(ex, ToolNames.IndexRemoveRepo);
        }
    }

    /// <summary>
    /// Destructive tools take no auto-default (PHASE-21-10): a missing <c>repo_path</c> is
    /// <c>INVALID_ARGUMENT</c>, naming the repos this process knows.
    /// </summary>
    private (string? RepoPath, ToolCallResult? Error) RequireExplicitRepoPath(JsonObject? args, string tool)
    {
        var repoPath = (args?["repo_path"] as JsonValue)?.TryGetValue<string>(out var v) == true ? v : null;
        if (!string.IsNullOrWhiteSpace(repoPath))
            return (repoPath, null);

        var known = _repoRegistry.KnownRepos;
        var knownText = known.Count > 0 ? string.Join(", ", known) : "none";
        return (null, Err(CodeMapError.InvalidArgument(
            $"{tool} deletes data and needs an explicit repo_path (known repos: {knownText}).")));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        var solutionPath = args?["solution_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return Error("repo_path is required");

        // Auto-discover solution if not provided or not found
        var resolved = DiscoverSolutionPath(repoPath, solutionPath);
        if (resolved is null)
        {
            if (!string.IsNullOrEmpty(solutionPath))
                return Error($"solution_path not found: {solutionPath} (also tried alternate extension)");
            return Error($"No .sln or .slnx file found in {repoPath}. Provide solution_path explicitly.");
        }
        solutionPath = resolved;

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
            CommitSha commitSha;
            var commitShaStr = args?["commit_sha"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(commitShaStr))
            {
                // Try direct parse first (full 40-char SHA)
                if (commitShaStr.Length == 40 && commitShaStr.All(c => char.IsAsciiHexDigitLower(c)))
                {
                    commitSha = CommitSha.From(commitShaStr);
                }
                else
                {
                    // Resolve short SHA, branch name, tag, etc.
                    var resolvedSha = await _git.ResolveCommitAsync(repoPath!, commitShaStr, ct).ConfigureAwait(false);
                    if (resolvedSha is null)
                        return Error($"Could not resolve commit '{commitShaStr}'. Provide a full 40-char SHA or omit commit_sha to use HEAD.");
                    commitSha = resolvedSha.Value;
                }
            }
            else
                commitSha = await _git.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);

            // Step 1: Check local (existing behavior)
            var exists = await _store.BaselineExistsAsync(repoId, commitSha, ct).ConfigureAwait(false);
            if (exists)
            {
                _logger.LogInformation("index_ensure_baseline: baseline already exists for {Sha}", commitSha.Value[..8]);
                _repoRegistry.Register(repoPath!);
                var skipped = new EnsureBaselineResponse(commitSha, AlreadyExisted: true, Stats: null);
                return new ToolCallResult(JsonSerializer.Serialize(skipped, CodeMapJsonOptions.Default));
            }

            // Step 2: Check shared cache
            var pulledPath = await _cache.PullAsync(repoId, commitSha, ct).ConfigureAwait(false);
            if (pulledPath is not null)
            {
                // Verify the pulled DB is now available locally
                var pulledExists = await _store.BaselineExistsAsync(repoId, commitSha, ct).ConfigureAwait(false);
                if (pulledExists)
                {
                    _logger.LogInformation(
                        "index_ensure_baseline: baseline {Sha} pulled from shared cache", commitSha.Value[..8]);
                    _repoRegistry.Register(repoPath!);
                    var cached = new EnsureBaselineResponse(commitSha, AlreadyExisted: true, Stats: null, FromCache: true);
                    return new ToolCallResult(JsonSerializer.Serialize(cached, CodeMapJsonOptions.Default));
                }
            }

            // Step 3: Build locally via Roslyn (existing behavior)
            _logger.LogInformation("index_ensure_baseline: compiling {SolutionPath}", solutionPath);
            var compilationResult = await _compiler.CompileAndExtractAsync(solutionPath, ct).ConfigureAwait(false);

            if (compilationResult.Symbols.Count == 0 && compilationResult.Stats.Confidence == Core.Enums.Confidence.Low)
                return Err(CodeMapError.CompilationFailed(
                    "Compilation produced no symbols. Check build errors.",
                    [solutionPath]));

            // Store with repoRootPath (ADR-012)
            await _store.CreateBaselineAsync(repoId, commitSha, compilationResult, repoPath, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "index_ensure_baseline: indexed {Symbols} symbols, {Refs} refs in {Ms:F1}ms",
                compilationResult.Stats.SymbolCount,
                compilationResult.Stats.ReferenceCount,
                compilationResult.Stats.ElapsedSeconds * 1000);

            // PHASE-21-12: memory right after a build (the compiler's workspace is disposed, not yet collected).
            _logger.LogInformation(MemorySnapshot.LogTemplate, MemorySnapshot.Capture().LogArgs("baseline_built"));
            _activity?.MarkHeavyWork("baseline_built");

            // Step 4: Push to shared cache — fire-and-forget for errors. Never share a baseline this
            // machine degraded (unrestored checkout, generator load failure): another machine pulling
            // it would get a wrong answer for a commit it could index completely (ADR-053, ADR-054).
            var degraded = compilationResult.Stats.ProjectDiagnostics?
                .Where(Core.Models.SemanticLevels.IsEnvironmentDegraded)
                .Select(d => d.ProjectName)
                .ToList();
            if (degraded is { Count: > 0 })
                _logger.LogInformation(
                    "index_ensure_baseline: {Sha} is not pushed to a shared cache (if one is configured) — environment-degraded project(s): {Projects}",
                    commitSha.Value[..8], string.Join(", ", degraded));
            else
                await _cache.PushAsync(repoId, commitSha, ct).ConfigureAwait(false);

            _repoRegistry.Register(repoPath!);
            var response = new EnsureBaselineResponse(commitSha, AlreadyExisted: false, Stats: compilationResult.Stats);
            return new ToolCallResult(JsonSerializer.Serialize(response, CodeMapJsonOptions.Default));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index_ensure_baseline failed for {SolutionPath}", solutionPath);
            return Classified(ex, ToolNames.IndexEnsureBaseline);
        }
    }

    /// <summary>
    /// Discovers the solution file path. Handles four cases:
    /// 1. Provided path exists → return it.
    /// 2. Provided path doesn't exist → try alternate extension (.sln ↔ .slnx).
    /// 3. No path provided → scan repo root for *.slnx then *.sln.
    /// 4. No solution file → fall back to a single project file (.csproj/.vbproj/.fsproj)
    ///    at repo root, or in the only direct child directory containing one.
    ///    Multiple ambiguous candidates → null.
    /// Returns null if no solution or single-project file is found.
    /// </summary>
    internal static string? DiscoverSolutionPath(string repoPath, string? providedPath)
    {
        // Case 1: Provided and exists
        if (!string.IsNullOrEmpty(providedPath) && File.Exists(providedPath))
            return providedPath;

        // Case 2: Provided but doesn't exist — try alternate extension
        if (!string.IsNullOrEmpty(providedPath))
        {
            var ext = Path.GetExtension(providedPath);
            var altExt = ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? ".sln" : ".slnx";
            var altPath = Path.ChangeExtension(providedPath, altExt);
            if (File.Exists(altPath))
                return altPath;
            return null;
        }

        // Case 3: Not provided — scan repo root (prefer .slnx)
        if (!Directory.Exists(repoPath))
            return null;

        var slnxFiles = Directory.GetFiles(repoPath, "*.slnx", SearchOption.TopDirectoryOnly);
        if (slnxFiles.Length > 0)
            return slnxFiles[0];

        var slnFiles = Directory.GetFiles(repoPath, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length > 0)
            return slnFiles[0];

        // Case 4: Project-file fallback. `dotnet new` templates (e.g. `blazor`)
        // ship without a solution; recognise a single project at the root or in
        // a single direct subdirectory.
        var rootProjects = ListProjectFiles(repoPath);
        if (rootProjects.Length == 1)
            return rootProjects[0];
        if (rootProjects.Length > 1)
            return null;

        var subdirs = Directory.GetDirectories(repoPath);
        var allChildProjects = new List<string>();
        foreach (var subdir in subdirs)
        {
            allChildProjects.AddRange(ListProjectFiles(subdir));
        }
        if (allChildProjects.Count == 1)
            return allChildProjects[0];

        return null;
    }

    private static string[] ListProjectFiles(string directory) =>
        [
            .. Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly),
            .. Directory.GetFiles(directory, "*.vbproj", SearchOption.TopDirectoryOnly),
            .. Directory.GetFiles(directory, "*.fsproj", SearchOption.TopDirectoryOnly),
        ];

    /// <summary>
    /// Validation failure (bad path, unresolvable commit, missing argument). Previously hard-coded
    /// <c>COMPILATION_FAILED</c> for every failure (ADR-041).
    /// </summary>
    private static ToolCallResult Error(string message) => Err(CodeMapError.InvalidArgument(message));

    private static ToolCallResult Err(CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);

    /// <summary>Handler outer-catch mapping — see <see cref="HandlerHelpers.ClassifyException"/>.</summary>
    private static ToolCallResult Classified(Exception ex, string operation) =>
        Err(HandlerHelpers.ClassifyException(ex, operation, workspaceId: null));

    private static ToolCallResult ScannerNotConfigured(string tool) =>
        Err(CodeMapError.InternalError($"{tool} is not available (scanner not configured)"));

    // ── Response type ──────────────────────────────────────────────────────────

    private record EnsureBaselineResponse(
        CommitSha CommitSha,
        bool AlreadyExisted,
        IndexStats? Stats,
        bool FromCache = false);
}
