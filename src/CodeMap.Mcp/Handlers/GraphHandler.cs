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
/// Handles the <c>graph.callers</c>, <c>graph.callees</c>, and <c>graph.trace_feature</c> MCP tools.
/// </summary>
/// <remarks>
/// <b>graph.callers / graph.callees</b> params: repo_path, symbol_id (required);
/// workspace_id, depth, limit_per_level (optional).
/// depth: clamped to [1, 6]; default 1. limit_per_level: clamped to [1, 500]; default 20.
///
/// <b>graph.trace_feature</b> params: repo_path, entry_point (required);
/// workspace_id, depth, limit (optional).
/// entry_point accepts FQN or stable_id (sym_ prefix — stable_id is resolved to SymbolId first).
/// depth: clamped to [1, 6]; default 3. limit: clamped to [1, 500]; default 100.
/// Returns a recursive tree annotated with architectural facts at each node.
///
/// All tools return INVALID_ARGUMENT if required params are missing.
/// </remarks>
public sealed class GraphHandler
{
    private const int DefaultDepth = 1;
    private const int DefaultLimitPerLevel = 20;
    private const int MaxDepthHardCap = 6;
    private const int MaxLimitHardCap = 500;

    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly IMcpSymbolResolver _resolver;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<GraphHandler> _logger;

    public GraphHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        IMcpSymbolResolver resolver,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<GraphHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _resolver = resolver;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    private const int DefaultTraceDepth = 3;
    private const int DefaultTraceLimit = 100;
    private const int MaxTraceDepthCap = 6;
    private const int MaxTraceLimitCap = 500;

    /// <summary>Registers graph.callers, graph.callees, and graph.trace_feature into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "graph.trace_feature",
            "Traces a feature end-to-end starting from an entry point method or endpoint handler. Returns a hierarchical call tree annotated with architectural facts (endpoints, config, DB tables, DI registrations) at each node. Accepts either entry_point (exact symbol ID) or name (resolved via search).",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware tracing"),
                    ["entry_point"] = Prop("string", "Symbol ID (FQN) or stable_id (sym_ prefix) of the entry method. Provide either entry_point (exact) or name (resolved)."),
                    ["name"] = McpToolHandlers.NameProp(),
                    ["name_filter"] = McpToolHandlers.NameFilterProp(),
                    ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max call depth to trace (default: {DefaultTraceDepth}, max: {MaxTraceDepthCap})" },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max nodes to traverse (default: {DefaultTraceLimit}, max: {MaxTraceLimitCap})" },
                }),
            HandleTraceFeatureAsync));

        registry.Register(new ToolDefinition(
            "graph.callers",
            "Find all callers of a C# symbol, traversing the call graph up to the specified depth. Accepts either symbol_id (exact) or name (resolved via search). In DI codebases, when the target method implements an interface, the response carries an interface_implementation_hint listing the interface members and an estimated count of callers that route through them. Pass follow_interface=true to union those into the result set.",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware traversal"),
                    ["symbol_id"] = Prop("string", "Symbol to find callers of (documentation comment ID format). Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = McpToolHandlers.NameProp(),
                    ["name_filter"] = McpToolHandlers.NameFilterProp(),
                    ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "Max traversal depth (default: 1, max: 6)" },
                    ["limit_per_level"] = new JsonObject { ["type"] = "integer", ["description"] = "Max nodes per BFS level (default: 20, max: 500)" },
                    ["follow_interface"] = new JsonObject { ["type"] = "boolean", ["description"] = "Default false. When true and the target implements an interface, union callers of the interface members into the result and dedupe. Off by default; the hint surfaces availability either way." },
                }),
            HandleCallersAsync));

        registry.Register(new ToolDefinition(
            "graph.callees",
            "Find all symbols called by a C# symbol, traversing the call graph down to the specified depth. Accepts either symbol_id (exact) or name (resolved via search).",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware traversal"),
                    ["symbol_id"] = Prop("string", "Symbol to find callees of (documentation comment ID format). Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = McpToolHandlers.NameProp(),
                    ["name_filter"] = McpToolHandlers.NameFilterProp(),
                    ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "Max traversal depth (default: 1, max: 6)" },
                    ["limit_per_level"] = new JsonObject { ["type"] = "integer", ["description"] = "Max nodes per BFS level (default: 20, max: 500)" },
                }),
            HandleCalleesAsync));
    }

    internal async Task<ToolCallResult> HandleTraceFeatureAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var depth = Math.Clamp(args.GetInt("depth", DefaultTraceDepth), 1, MaxTraceDepthCap);
        var limit = Math.Clamp(args.GetInt("limit", DefaultTraceLimit), 1, MaxTraceLimitCap);

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args, repoPath!);

        // Resolve entry point: stable_id (sym_ prefix), FQN via entry_point, or name via resolver.
        // entry_point and symbol_id are synonyms here for compatibility with the resolver.
        Core.Types.SymbolId entryPoint;
        var entryPointStr = args?["entry_point"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(entryPointStr) && entryPointStr.StartsWith("sym_", StringComparison.Ordinal))
        {
            var stableId = new Core.Types.StableId(entryPointStr);
            var cardResult = await _queryEngine.GetSymbolByStableIdAsync(routing, stableId, ct).ConfigureAwait(false);
            if (cardResult.IsFailure) return Err(cardResult.Error);
            entryPoint = cardResult.Value.Data.SymbolId;
        }
        else if (!string.IsNullOrEmpty(entryPointStr))
        {
            entryPoint = Core.Types.SymbolId.From(entryPointStr);
        }
        else
        {
            var resolved = await _resolver.ResolveAsync(args, routing, ct).ConfigureAwait(false);
            if (resolved.Error is { } rErr) return Err(rErr);
            entryPoint = resolved.Symbol!.Value;
        }

        var result = await _queryEngine.TraceFeatureAsync(routing, entryPoint, depth, limit, ct).ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    internal async Task<ToolCallResult> HandleCallersAsync(JsonObject? args, CancellationToken ct)
        => await HandleGraphAsync(args, ct, callers: true).ConfigureAwait(false);

    internal async Task<ToolCallResult> HandleCalleesAsync(JsonObject? args, CancellationToken ct)
        => await HandleGraphAsync(args, ct, callers: false).ConfigureAwait(false);

    private async Task<ToolCallResult> HandleGraphAsync(JsonObject? args, CancellationToken ct, bool callers)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var depth = Math.Clamp(args.GetInt("depth", DefaultDepth), 1, MaxDepthHardCap);
        var limitPerLevel = Math.Clamp(args.GetInt("limit_per_level", DefaultLimitPerLevel), 1, MaxLimitHardCap);

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
        var routing = BuildRouting(repoId, sha, args, repoPath!);

        var resolved = await _resolver.ResolveAsync(args, routing, ct).ConfigureAwait(false);
        if (resolved.Error is { } rErr) return Err(rErr);
        var symbolId = resolved.Symbol!.Value;
        var symbolIdStr = symbolId.Value;
        var budgets = new BudgetLimits(maxDepth: depth, maxReferences: limitPerLevel);

        // Validate that the symbol is a method-like symbol, not a type.
        // Passing a class/interface to graph.callers returns 0 with no explanation — better to be helpful.
        var cardResult = await _queryEngine.GetSymbolCardAsync(routing, symbolId, ct).ConfigureAwait(false);
        if (cardResult.IsFailure && cardResult.Error.Code == ErrorCodes.NotFound)
            return HandlerHelpers.ErrWithNotFoundSuggestion(cardResult.Error, symbolIdStr);
        if (cardResult.IsSuccess && cardResult.Value?.Data is { } card
            && card.Kind is SymbolKind.Class or SymbolKind.Interface
                or SymbolKind.Struct or SymbolKind.Record)
        {
            var toolName = callers ? "graph.callers" : "graph.callees";
            return InvalidArg(
                $"{toolName} works on methods and properties, not types. " +
                $"Try: refs.find to see references to {card.FullyQualifiedName}, " +
                $"or search for a specific method on {card.FullyQualifiedName}.");
        }

        var followInterface = callers && args.GetBool("follow_interface", false);

        var result = callers
            ? await _queryEngine.GetCallersAsync(routing, symbolId, depth, limitPerLevel, budgets, ct, followInterface).ConfigureAwait(false)
            : await _queryEngine.GetCalleesAsync(routing, symbolId, depth, limitPerLevel, budgets, ct).ConfigureAwait(false);

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
