namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>codemap.export</c> MCP tool.
/// </summary>
/// <remarks>
/// <b>codemap.export</b> params: repo_path (required), detail (summary/standard/full), format (markdown/json),
/// max_tokens, section_filter, workspace_id (all optional).
/// Exports the indexed codebase as a self-contained markdown or JSON document suitable for pasting into any LLM.
/// Returns INVALID_ARGUMENT if repo_path is missing.
/// </remarks>
public sealed class ExportHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<ExportHandler> _logger;

    public ExportHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<ExportHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    /// <summary>Registers codemap.export into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "codemap.export",
            "Export the indexed codebase as a self-contained markdown or JSON document for pasting into any LLM chat interface. Supports summary/standard/full detail levels and a token budget.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(),
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Absolute path to the repository root",
                    },
                    ["detail"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "summary", "standard", "full" },
                        ["description"] = "Detail level: summary (overview only), standard (+ public API, dependencies, interfaces), full (+ all symbols, reference matrix). Default: standard.",
                    },
                    ["format"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "markdown", "json" },
                        ["description"] = "Output format: markdown (default) or json.",
                    },
                    ["max_tokens"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Token budget for the exported content. Defaults to 4000.",
                    },
                    ["section_filter"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Optional: list of sections to include (public_api, dependencies, interfaces, all_symbols, references).",
                    },
                    ["workspace_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional: workspace ID for overlay-aware query",
                    },
                },
            },
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var detail = args?["detail"]?.GetValue<string>() ?? "standard";
        var format = args?["format"]?.GetValue<string>() ?? "markdown";
        var maxTokens = args.GetInt("max_tokens", 4000);

        string[]? sectionFilter = null;
        if (args?["section_filter"] is JsonArray arr)
            sectionFilter = arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray();

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args, repoPath!);

        var result = await _queryEngine.ExportAsync(routing, detail, format, maxTokens, sectionFilter, repoPath!, ct)
                                       .ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RoutingContext BuildRouting(RepoId repoId, CommitSha sha, JsonObject? args, string repoPath)
    {
        var workspaceIdStr = HandlerHelpers.ResolveWorkspaceId(args, repoPath, _stickyRegistry);
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
