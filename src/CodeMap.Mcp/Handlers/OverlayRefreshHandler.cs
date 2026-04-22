namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Serialization;
using CodeMap.Query;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>index.refresh_overlay</c> MCP tool.
/// Triggers incremental reindexing for changed files in a workspace.
/// </summary>
/// <remarks>
/// <b>JSON params:</b> repo_path, workspace_id (both required); file_paths (optional string array).
/// When file_paths is omitted, changed files are auto-detected via git diff against the
/// workspace's baseline commit. Explicit file_paths override git-diff detection.
/// Returns INVALID_ARGUMENT if required params are missing.
/// Resolution pass runs automatically after reindex (skips SyntaxOnly baselines).
/// </remarks>
public sealed class OverlayRefreshHandler
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly IGitService _gitService;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<OverlayRefreshHandler> _logger;

    public OverlayRefreshHandler(
        WorkspaceManager workspaceManager,
        IGitService gitService,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<OverlayRefreshHandler> logger)
    {
        _workspaceManager = workspaceManager;
        _gitService = gitService;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    /// <summary>Registers the <c>index.refresh_overlay</c> tool.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "index.refresh_overlay",
            "Incrementally reindex changed files for a workspace overlay.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to repository root"),
                    ["workspace_id"] = Prop("string", "Workspace identifier to refresh"),
                    ["file_paths"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Specific files to reindex (default: auto-detect via git diff)",
                    },
                },
            },
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var workspaceStr = HandlerHelpers.ResolveWorkspaceId(args, repoPath!, _stickyRegistry);
        if (string.IsNullOrEmpty(workspaceStr)) return InvalidArg("workspace_id is required");

        try
        {
            var repoId = await _gitService.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
            var workspaceId = WorkspaceId.From(workspaceStr);

            // Parse optional file_paths array
            List<FilePath>? filePaths = null;
            var filePathsNode = args?["file_paths"] as JsonArray;
            if (filePathsNode is { Count: > 0 })
            {
                filePaths = filePathsNode
                    .Select(n => n?.GetValue<string>())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => FilePath.From(s!))
                    .ToList();
            }

            var result = await _workspaceManager.RefreshOverlayAsync(repoId, workspaceId, filePaths, ct)
                                                  .ConfigureAwait(false);

            return result.Match(Ok, Err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "index.refresh_overlay failed for {RepoPath}", repoPath);
            return InvalidArg($"index.refresh_overlay failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ToolCallResult Ok<T>(T value) =>
        new(JsonSerializer.Serialize(value, CodeMapJsonOptions.Default));

    private static ToolCallResult Err(CodeMap.Core.Errors.CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);

    private static ToolCallResult InvalidArg(string message) =>
        Err(CodeMap.Core.Errors.CodeMapError.InvalidArgument(message));

    private static JsonObject Prop(string type, string? description = null)
    {
        var obj = new JsonObject { ["type"] = type };
        if (description is not null) obj["description"] = description;
        return obj;
    }
}
