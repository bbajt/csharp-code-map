namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;
using CodeMap.Query;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>index.ensure_baseline</c>, <c>index.list_baselines</c> MCP tools.
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
    private readonly ILogger<IndexHandler> _logger;

    public IndexHandler(
        IGitService git,
        IRoslynCompiler compiler,
        ISymbolStore store,
        IBaselineCacheManager cache,
        ILogger<IndexHandler> logger,
        IBaselineScanner? scanner = null,
        WorkspaceManager? workspaceManager = null)
    {
        _git = git;
        _compiler = compiler;
        _store = store;
        _cache = cache;
        _scanner = scanner;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Registers the <c>index.list_baselines</c>, <c>index.cleanup</c>,
    /// and <c>index.ensure_baseline</c> tools into the ToolRegistry.
    /// </summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "index.list_baselines",
            "List all cached baselines for a repository, showing commit SHA, creation date, file size, and whether each is the current HEAD or referenced by an active workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray((JsonNode?)"repo_path"),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root" },
                },
            },
            HandleListBaselinesAsync));

        registry.Register(new ToolDefinition(
            "index.cleanup",
            "Remove old cached baselines to reclaim disk space. Current HEAD and workspace-referenced baselines are never deleted. Default is dry_run:true — set dry_run:false to actually delete.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray((JsonNode?)"repo_path"),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root" },
                    ["keep_count"] = new JsonObject { ["type"] = "integer", ["description"] = "Keep the N most recent baselines (default: 5)" },
                    ["older_than_days"] = new JsonObject { ["type"] = "integer", ["description"] = "Remove baselines older than N days" },
                    ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "If true, report what would be deleted without deleting (default: true)" },
                },
            },
            HandleCleanupAsync));

        registry.Register(new ToolDefinition(
            "index.ensure_baseline",
            "Build a semantic index for a .NET solution. Idempotent: returns immediately if the current commit is already indexed.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(
                    (JsonNode?)"repo_path",
                    (JsonNode?)"solution_path"),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the repository root" },
                    ["solution_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the .sln file" },
                    ["commit_sha"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: specific commit to index (default: HEAD)" },
                },
            },
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleListBaselinesAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return Error("repo_path is required");
        if (_scanner is null) return Error("index.list_baselines is not available (scanner not configured)");

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);

            CommitSha? currentHead = null;
            try
            {
                currentHead = await _git.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "index.list_baselines: could not resolve HEAD for {RepoPath}", repoPath);
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
            _logger.LogError(ex, "index.list_baselines failed for {RepoPath}", repoPath);
            return Error($"Failed to list baselines: {ex.Message}");
        }
    }

    internal async Task<ToolCallResult> HandleCleanupAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return Error("repo_path is required");
        if (_scanner is null) return Error("index.cleanup is not available (scanner not configured)");

        var keepCount = args?["keep_count"]?.GetValue<int>() ?? 5;
        var olderThanDays = args?["older_than_days"]?.GetValue<int>();
        var dryRun = args?["dry_run"]?.GetValue<bool>() ?? true;

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
            var currentHead = await _git.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);

            IReadOnlyList<CodeMap.Query.WorkspaceSummary> workspaces = _workspaceManager is not null
                ? await _workspaceManager.ListWorkspacesAsync(repoId, ct).ConfigureAwait(false)
                : [];
            var workspaceBaseCommits = workspaces
                .Select(w => w.BaseCommitSha)
                .ToHashSet();

            var response = await _scanner.CleanupBaselinesAsync(
                repoId, currentHead, workspaceBaseCommits,
                keepCount, olderThanDays, dryRun, ct).ConfigureAwait(false);

            var json = JsonSerializer.Serialize(response, CodeMapJsonOptions.Default);
            if (response.DryRun)
                json += "\n(Dry run — no files were actually deleted. Pass dry_run:false to delete.)";

            return new ToolCallResult(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index.cleanup failed for {RepoPath}", repoPath);
            return Error($"Cleanup failed: {ex.Message}");
        }
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        var solutionPath = args?["solution_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return Error("repo_path is required");
        if (string.IsNullOrEmpty(solutionPath)) return Error("solution_path is required");
        if (!File.Exists(solutionPath)) return Error($"solution_path not found: {solutionPath}");

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
            CommitSha commitSha;
            var commitShaStr = args?["commit_sha"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(commitShaStr))
                commitSha = CommitSha.From(commitShaStr);
            else
                commitSha = await _git.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);

            // Step 1: Check local (existing behavior)
            var exists = await _store.BaselineExistsAsync(repoId, commitSha, ct).ConfigureAwait(false);
            if (exists)
            {
                _logger.LogInformation("index.ensure_baseline: baseline already exists for {Sha}", commitSha.Value[..8]);
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
                        "index.ensure_baseline: baseline {Sha} pulled from shared cache", commitSha.Value[..8]);
                    var cached = new EnsureBaselineResponse(commitSha, AlreadyExisted: true, Stats: null, FromCache: true);
                    return new ToolCallResult(JsonSerializer.Serialize(cached, CodeMapJsonOptions.Default));
                }
            }

            // Step 3: Build locally via Roslyn (existing behavior)
            _logger.LogInformation("index.ensure_baseline: compiling {SolutionPath}", solutionPath);
            var compilationResult = await _compiler.CompileAndExtractAsync(solutionPath, ct).ConfigureAwait(false);

            if (compilationResult.Symbols.Count == 0 && compilationResult.Stats.Confidence == Core.Enums.Confidence.Low)
                return Error(CodeMapError.CompilationFailed(
                    "Compilation produced no symbols. Check build errors.",
                    [solutionPath]).Message);

            // Store with repoRootPath (ADR-012)
            await _store.CreateBaselineAsync(repoId, commitSha, compilationResult, repoPath, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "index.ensure_baseline: indexed {Symbols} symbols, {Refs} refs in {Ms:F1}ms",
                compilationResult.Stats.SymbolCount,
                compilationResult.Stats.ReferenceCount,
                compilationResult.Stats.ElapsedSeconds * 1000);

            // Step 4: Push to shared cache — fire-and-forget for errors
            await _cache.PushAsync(repoId, commitSha, ct).ConfigureAwait(false);

            var response = new EnsureBaselineResponse(commitSha, AlreadyExisted: false, Stats: compilationResult.Stats);
            return new ToolCallResult(JsonSerializer.Serialize(response, CodeMapJsonOptions.Default));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index.ensure_baseline failed for {SolutionPath}", solutionPath);
            return Error($"Indexing failed: {ex.Message}");
        }
    }

    private static ToolCallResult Error(string message) =>
        new(JsonSerializer.Serialize(
            new { code = "COMPILATION_FAILED", message },
            CodeMapJsonOptions.Default),
            IsError: true);

    // ── Response type ──────────────────────────────────────────────────────────

    private record EnsureBaselineResponse(
        CommitSha CommitSha,
        bool AlreadyExisted,
        IndexStats? Stats,
        bool FromCache = false);
}
