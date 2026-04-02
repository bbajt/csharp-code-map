namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>codemap.summarize</c> MCP tool.
/// </summary>
/// <remarks>
/// <b>codemap.summarize</b> params: repo_path (required), workspace_id, section_filter, max_items_per_section (all optional).
/// Generates a structured markdown summary of the indexed codebase covering all 8 FactKinds.
/// Returns INVALID_ARGUMENT if repo_path is missing.
/// Sections with zero items are omitted from output.
/// </remarks>
public sealed class SummaryHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly ILogger<SummaryHandler> _logger;

    public SummaryHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        ILogger<SummaryHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _logger = logger;
    }

    /// <summary>Registers codemap.summarize into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "codemap.summarize",
            "Generate a structured markdown summary of the indexed codebase — API surface, data layer, config, DI, middleware, resilience, error handling, and logging.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { JsonValue.Create("repo_path") },
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Absolute path to the repository root",
                    },
                    ["workspace_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional: workspace ID for overlay-aware query",
                    },
                    ["section_filter"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Optional: list of sections to include (overview, api, data, config, di, middleware, resilience, exceptions, logging, metrics)",
                    },
                    ["max_items_per_section"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum items per section (default: 50)",
                    },
                },
            },
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath))
            return Err(CodeMapError.InvalidArgument("repo_path is required"));

        var maxItems = args.GetInt("max_items_per_section", 50);

        string[]? sectionFilter = null;
        if (args?["section_filter"] is JsonArray arr)
            sectionFilter = arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray();

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args);

        var result = await _queryEngine.SummarizeAsync(routing, repoPath, sectionFilter, maxItems, ct)
                                       .ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext BuildRouting(RepoId repoId, CommitSha sha, JsonObject? args)
    {
        var workspaceIdStr = args?["workspace_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(workspaceIdStr))
        {
            var workspaceId = WorkspaceId.From(workspaceIdStr);
            return new RoutingContext(
                repoId: repoId,
                workspaceId: workspaceId,
                consistency: ConsistencyMode.Workspace,
                baselineCommitSha: sha);
        }
        return new RoutingContext(repoId: repoId, baselineCommitSha: sha);
    }

    private static ToolCallResult Ok<T>(T value) =>
        new(JsonSerializer.Serialize(value, CodeMapJsonOptions.Default));

    private static ToolCallResult Err(CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);
}
