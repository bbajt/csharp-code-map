namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Resolution;
using CodeMap.Mcp.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>types.hierarchy</c> MCP tool.
/// Returns the base type, implemented interfaces, and derived types for a C# type symbol.
/// </summary>
/// <remarks>
/// <b>JSON params:</b> repo_path, symbol_id (required); workspace_id (optional).
/// symbol_id should be a type symbol in documentation comment ID format (e.g. "T:MyNs.MyClass").
/// Returns INVALID_ARGUMENT if required params are missing.
/// System.Object is excluded from base type results (see CLAUDE.MD constraints).
/// </remarks>
public sealed class TypeHierarchyHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly IMcpSymbolResolver _resolver;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<TypeHierarchyHandler> _logger;

    public TypeHierarchyHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        IMcpSymbolResolver resolver,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<TypeHierarchyHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _resolver = resolver;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    /// <summary>Registers types.hierarchy into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "types.hierarchy",
            "Get the type hierarchy for a C# type: base class, implemented interfaces, and derived types. Accepts either symbol_id (exact) or name (resolved via search).",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware query"),
                    ["symbol_id"] = Prop("string", "Type symbol ID (documentation comment ID format, e.g. T:MyNs.MyClass). Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = McpToolHandlers.NameProp(),
                    ["name_filter"] = McpToolHandlers.NameFilterProp(),
                }),
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args, repoPath!);

        var resolved = await _resolver.ResolveAsync(args, routing, ct).ConfigureAwait(false);
        if (resolved.Error is { } rErr) return Err(rErr);
        var symbolId = resolved.Symbol!.Value;

        var result = await _queryEngine.GetTypeHierarchyAsync(routing, symbolId, ct).ConfigureAwait(false);
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
}
