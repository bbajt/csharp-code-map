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
/// Handles the <c>refs.find</c> MCP tool.
/// Finds all references to a symbol, with optional RefKind filter and workspace support.
/// </summary>
/// <remarks>
/// <b>JSON params:</b> repo_path, symbol_id (required); workspace_id, kind, resolution_state, limit (optional).
/// kind: Call | Read | Write | Instantiate | Override | Implementation (invalid value → INVALID_ARGUMENT).
/// resolution_state: resolved | unresolved (default: all — returns both resolved and unresolved).
/// limit: clamped to [1, 500]; default 50.
/// Returns INVALID_ARGUMENT if required params are missing or kind/resolution_state is invalid.
/// </remarks>
public sealed class RefsHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly IMcpSymbolResolver _resolver;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<RefsHandler> _logger;

    public RefsHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        IMcpSymbolResolver resolver,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<RefsHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _resolver = resolver;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    /// <summary>Registers the refs.find tool into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "refs.find",
            "Find all references to a C# symbol, optionally filtered by reference kind. Accepts either symbol_id (exact) or name (resolved via search).",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay refs"),
                    ["symbol_id"] = Prop("string", "Fully qualified symbol ID (e.g. M:MyNs.MyClass.MyMethod). Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = McpToolHandlers.NameProp(),
                    ["name_filter"] = McpToolHandlers.NameFilterProp(),
                    ["kind"] = Prop("string", "Filter by RefKind: Call, Read, Write, Instantiate, Override, Implementation"),
                    ["resolution_state"] = Prop("string", "Filter by resolution state: resolved, unresolved (default: all)"),
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max references (default: 50, max: 500)" },
                }),
            HandleFindRefsAsync));
    }

    internal async Task<ToolCallResult> HandleFindRefsAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        // Parse optional kind filter
        RefKind? kind = null;
        var kindStr = args?["kind"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(kindStr))
        {
            if (!Enum.TryParse<RefKind>(kindStr, ignoreCase: true, out var parsedKind))
                return InvalidArg($"Invalid RefKind '{kindStr}'. Valid values: Call, Read, Write, Instantiate, Override, Implementation");
            kind = parsedKind;
        }

        // Parse optional resolution_state filter
        ResolutionState? resolutionState = null;
        var resStateStr = args?["resolution_state"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(resStateStr))
        {
            if (!Enum.TryParse<ResolutionState>(resStateStr, ignoreCase: true, out var parsedResState))
                return InvalidArg($"Invalid resolution_state '{resStateStr}'. Valid values: resolved, unresolved");
            resolutionState = parsedResState;
        }

        var limitVal = args.GetInt("limit");
        BudgetLimits? budgets = limitVal is not null
            ? new BudgetLimits(maxReferences: Math.Clamp(limitVal.Value, 1, 500))
            : null;

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args, repoPath!);

        var resolved = await _resolver.ResolveAsync(args, routing, ct).ConfigureAwait(false);
        if (resolved.Error is { } rErr) return Err(rErr);
        var symbolId = resolved.Symbol!.Value;

        var result = await _queryEngine.FindReferencesAsync(routing, symbolId, kind, budgets, ct, resolutionState)
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
