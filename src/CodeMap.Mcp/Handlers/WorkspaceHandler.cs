namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;
using CodeMap.Query;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>workspace.create</c>, <c>workspace.reset</c>,
/// <c>workspace.list</c>, and <c>workspace.delete</c> MCP tools.
/// </summary>
/// <remarks>
/// <b>workspace.create</b> params: repo_path, workspace_id, solution_path (all required), commit_sha (optional).
/// Idempotent — safe to call again if the workspace already exists.
///
/// <b>workspace.reset</b> params: repo_path, workspace_id (both required).
/// Clears all overlay data, resets revision to 0.
///
/// <b>workspace.list</b> params: repo_path (required).
/// Returns all active workspaces with staleness, semantic level, and fact counts.
///
/// <b>workspace.delete</b> params: repo_path, workspace_id (both required).
/// Permanently removes the workspace and its overlay DB.
/// Use workspace.reset instead to keep the workspace but clear its data.
///
/// All operations return INVALID_ARGUMENT if required params are missing.
/// </remarks>
public sealed class WorkspaceHandler
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly IGitService _gitService;
    private readonly ILogger<WorkspaceHandler> _logger;

    public WorkspaceHandler(
        WorkspaceManager workspaceManager,
        IGitService gitService,
        ILogger<WorkspaceHandler> logger)
    {
        _workspaceManager = workspaceManager;
        _gitService = gitService;
        _logger = logger;
    }

    /// <summary>Registers all workspace MCP tools.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "workspace.create",
            "Create an isolated workspace session for incremental overlay indexing.",
            BuildSchema(
                required: ["repo_path", "workspace_id", "solution_path"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to repository root"),
                    ["workspace_id"] = Prop("string", "Unique workspace identifier for this agent session"),
                    ["solution_path"] = Prop("string", "Absolute path to .sln file"),
                    ["commit_sha"] = Prop("string", "Baseline commit (default: HEAD)"),
                }),
            HandleCreateAsync));

        registry.Register(new ToolDefinition(
            "workspace.reset",
            "Discard all overlay data for a workspace and reset to the baseline state.",
            BuildSchema(
                required: ["repo_path", "workspace_id"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to repository root"),
                    ["workspace_id"] = Prop("string", "Workspace identifier to reset"),
                }),
            HandleResetAsync));

        registry.Register(new ToolDefinition(
            "workspace.list",
            "List all active workspaces for a repository, including staleness and quality metadata.",
            BuildSchema(
                required: ["repo_path"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to repository root"),
                }),
            HandleListAsync));

        registry.Register(new ToolDefinition(
            "workspace.delete",
            "Permanently delete a workspace and its overlay data. Use workspace.reset to keep the workspace but clear its data.",
            BuildSchema(
                required: ["repo_path", "workspace_id"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to repository root"),
                    ["workspace_id"] = Prop("string", "Workspace identifier to delete"),
                }),
            HandleDeleteAsync));
    }

    // ── workspace.create ──────────────────────────────────────────────────────

    internal async Task<ToolCallResult> HandleCreateAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        var workspaceStr = args?["workspace_id"]?.GetValue<string>();
        var solutionPath = args?["solution_path"]?.GetValue<string>();

        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");
        if (string.IsNullOrEmpty(workspaceStr)) return InvalidArg("workspace_id is required");
        if (string.IsNullOrEmpty(solutionPath)) return InvalidArg("solution_path is required");

        try
        {
            var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);

            CommitSha sha;
            var commitShaStr = args?["commit_sha"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(commitShaStr))
                sha = CommitSha.From(commitShaStr);
            else
                sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);

            var workspaceId = WorkspaceId.From(workspaceStr);
            var result = await _workspaceManager.CreateWorkspaceAsync(
                repoId, workspaceId, sha, solutionPath, repoPath, ct).ConfigureAwait(false);

            return result.Match(Ok, Err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "workspace.create failed for {RepoPath}", repoPath);
            return InvalidArg($"workspace.create failed: {ex.Message}");
        }
    }

    // ── workspace.reset ───────────────────────────────────────────────────────

    internal async Task<ToolCallResult> HandleResetAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        var workspaceStr = args?["workspace_id"]?.GetValue<string>();

        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");
        if (string.IsNullOrEmpty(workspaceStr)) return InvalidArg("workspace_id is required");

        try
        {
            var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
            var workspaceId = WorkspaceId.From(workspaceStr);
            var result = await _workspaceManager.ResetWorkspaceAsync(repoId, workspaceId, ct)
                                                      .ConfigureAwait(false);

            return result.Match(Ok, Err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "workspace.reset failed for {RepoPath}", repoPath);
            return InvalidArg($"workspace.reset failed: {ex.Message}");
        }
    }

    // ── workspace.list ────────────────────────────────────────────────────────

    internal async Task<ToolCallResult> HandleListAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();

        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");

        try
        {
            var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
            var workspaces = await _workspaceManager.ListWorkspacesAsync(repoId, ct).ConfigureAwait(false);
            var currentHead = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);

            return Ok(new WorkspaceListResponse(workspaces, currentHead));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "workspace.list failed for {RepoPath}", repoPath);
            return InvalidArg($"workspace.list failed: {ex.Message}");
        }
    }

    // ── workspace.delete ──────────────────────────────────────────────────────

    internal async Task<ToolCallResult> HandleDeleteAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        var workspaceStr = args?["workspace_id"]?.GetValue<string>();

        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");
        if (string.IsNullOrEmpty(workspaceStr)) return InvalidArg("workspace_id is required");

        try
        {
            var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
            var workspaceId = WorkspaceId.From(workspaceStr);

            var existed = _workspaceManager.GetWorkspaceInfo(repoId, workspaceId) != null;
            await _workspaceManager.DeleteWorkspaceAsync(repoId, workspaceId, ct).ConfigureAwait(false);

            return Ok(new WorkspaceDeleteResponse(workspaceId, existed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "workspace.delete failed for {RepoPath}", repoPath);
            return InvalidArg($"workspace.delete failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ToolCallResult Ok<T>(T value) =>
        new(JsonSerializer.Serialize(value, CodeMapJsonOptions.Default));

    private static ToolCallResult Err(CodeMap.Core.Errors.CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);

    private static ToolCallResult InvalidArg(string message) =>
        Err(CodeMap.Core.Errors.CodeMapError.InvalidArgument(message));

    private static JsonObject BuildSchema(string[] required, JsonObject properties) =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["properties"] = properties,
        };

    private static JsonObject Prop(string type, string? description = null)
    {
        var obj = new JsonObject { ["type"] = type };
        if (description is not null) obj["description"] = description;
        return obj;
    }
}
