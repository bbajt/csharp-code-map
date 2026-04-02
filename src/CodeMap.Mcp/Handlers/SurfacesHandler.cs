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
/// Handles the <c>surfaces.list_endpoints</c>, <c>surfaces.list_config_keys</c>,
/// and <c>surfaces.list_db_tables</c> MCP tools.
/// </summary>
/// <remarks>
/// <b>surfaces.list_endpoints</b> params: repo_path (required), workspace_id, path_filter, http_method, limit (all optional).
/// Detects controller-based ([HttpGet], [Route]) and minimal API (MapGet, MapPost, etc.) endpoints.
///
/// <b>surfaces.list_config_keys</b> params: repo_path (required), workspace_id, key_filter, limit (all optional).
/// Detects IConfiguration indexer, GetValue, GetSection, and Configure&lt;T&gt; patterns.
///
/// <b>surfaces.list_db_tables</b> params: repo_path (required), workspace_id, table_filter, limit (all optional).
/// Detects DbSet&lt;T&gt; properties, [Table] attributes, and raw SQL table names.
///
/// Workspace merge: endpoints and config keys use overlay-wins-by-file;
/// DB tables use overlay-wins-by-table-name (tables are aggregated, not file-scoped).
/// All operations return INVALID_ARGUMENT if repo_path is missing.
/// </remarks>
public sealed class SurfacesHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly ILogger<SurfacesHandler> _logger;

    public SurfacesHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        ILogger<SurfacesHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _logger = logger;
    }

    /// <summary>Registers surfaces.list_endpoints, surfaces.list_config_keys, and surfaces.list_db_tables into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "surfaces.list_endpoints",
            "List HTTP endpoints exposed by the ASP.NET solution (controller-based and minimal API routes).",
            BuildSchema(
                required: ["repo_path"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware query"),
                    ["path_filter"] = Prop("string", "Optional: prefix match on route path (e.g. '/api/orders')"),
                    ["http_method"] = PropEnum("string", "Optional: filter by HTTP method",
                                          ["GET", "POST", "PUT", "DELETE", "PATCH"]),
                    ["limit"] = Prop("integer", "Maximum number of endpoints to return (default: 50)"),
                }),
            HandleAsync));

        registry.Register(new ToolDefinition(
            "surfaces.list_config_keys",
            "List configuration keys used by the ASP.NET solution (IConfiguration indexer, GetValue, GetSection, Options pattern).",
            BuildSchema(
                required: ["repo_path"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware query"),
                    ["key_filter"] = Prop("string", "Optional: prefix match on config key (e.g. 'App:')"),
                    ["limit"] = Prop("integer", "Maximum number of keys to return (default: 50)"),
                }),
            HandleConfigKeysAsync));

        registry.Register(new ToolDefinition(
            "surfaces.list_db_tables",
            "List database tables referenced by the solution (EF Core DbSet<T>, [Table] attributes, raw SQL strings).",
            BuildSchema(
                required: ["repo_path"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware query"),
                    ["table_filter"] = Prop("string", "Optional: prefix match on table name (e.g. 'Order')"),
                    ["limit"] = Prop("integer", "Maximum number of tables to return (default: 50)"),
                }),
            HandleDbTablesAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");

        var pathFilter = args?["path_filter"]?.GetValue<string>();
        var httpMethod = args?["http_method"]?.GetValue<string>();
        var limit = args.GetInt("limit", 50);

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args);

        var result = await _queryEngine.ListEndpointsAsync(routing, pathFilter, httpMethod, limit, ct)
                                       .ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    internal async Task<ToolCallResult> HandleConfigKeysAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");

        var keyFilter = args?["key_filter"]?.GetValue<string>();
        var limit = args.GetInt("limit", 50);

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args);

        var result = await _queryEngine.ListConfigKeysAsync(routing, keyFilter, limit, ct)
                                       .ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    internal async Task<ToolCallResult> HandleDbTablesAsync(JsonObject? args, CancellationToken ct)
    {
        var repoPath = args?["repo_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(repoPath)) return InvalidArg("repo_path is required");

        var tableFilter = args?["table_filter"]?.GetValue<string>();
        var limit = args.GetInt("limit", 50);

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args);

        var result = await _queryEngine.ListDbTablesAsync(routing, tableFilter, limit, ct)
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

    private static ToolCallResult InvalidArg(string message) => Err(CodeMapError.InvalidArgument(message));

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

    private static JsonObject PropEnum(string type, string? description, string[] values)
    {
        var obj = new JsonObject { ["type"] = type };
        if (description is not null) obj["description"] = description;
        obj["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return obj;
    }
}
