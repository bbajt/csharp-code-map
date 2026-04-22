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
/// Handles the <c>symbols.get_context</c> MCP tool (#25).
/// </summary>
/// <remarks>
/// <b>symbols.get_context</b> params: repo_path, symbol_id (required);
/// workspace_id, callee_depth, max_callees, include_code (optional).
///
/// Returns the primary symbol's card with source code, plus cards of its immediate callees
/// (each with source code). One call replaces the typical
/// search → get_card → get_definition_span → get_callees chain.
///
/// callee_depth: clamped to [0, 2]; default 1.
/// max_callees: clamped to [0, 25]; default 10.
/// include_code: default true. Set false for metadata-only lookups.
///
/// entry_point (symbol_id) accepts both FQN and sym_ stable ID.
/// </remarks>
public sealed class ContextHandler
{
    private const int DefaultCalleeDepth = 1;
    private const int DefaultMaxCallees = 10;

    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly IMcpSymbolResolver _resolver;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<ContextHandler> _logger;

    /// <summary>Initializes the ContextHandler with required dependencies.</summary>
    public ContextHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        IMcpSymbolResolver resolver,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<ContextHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _resolver = resolver;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    /// <summary>Registers symbols.get_context into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "symbols.get_context",
            "Get a symbol's full context in one call: card + source code + callee cards with code. " +
            "Replaces the typical search → get_card → get_definition_span → graph.callees chain.",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root. Optional when exactly one repo is indexed in this session."),
                    ["symbol_id"] = Prop("string", "FQN (e.g. M:Namespace.Class.Method) or sym_ stable ID. Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = McpToolHandlers.NameProp(),
                    ["name_filter"] = McpToolHandlers.NameFilterProp(),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay-aware context"),
                    ["callee_depth"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = $"Depth of callee expansion (default: {DefaultCalleeDepth}, range: 0–2). " +
                                          "0 = no callees, 1 = immediate callees, 2 = callees of callees.",
                    },
                    ["max_callees"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = $"Max callee cards to include (default: {DefaultMaxCallees}, max: 25).",
                    },
                    ["include_code"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include source code in all cards (default: true). Set false for metadata-only.",
                    },
                }),
            HandleGetContextAsync));
    }

    internal async Task<ToolCallResult> HandleGetContextAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var calleeDepth = args.GetInt("callee_depth", DefaultCalleeDepth);
        var maxCallees = args.GetInt("max_callees", DefaultMaxCallees);
        var includeCode = args?["include_code"]?.GetValue<bool>() ?? true;

        calleeDepth = Math.Clamp(calleeDepth, 0, 2);
        maxCallees = Math.Clamp(maxCallees, 0, 25);

        var routingResult = await BuildRoutingResultAsync(repoPath!, args, ct).ConfigureAwait(false);
        if (routingResult.IsFailure) return routingResult.Error;

        var explicitSymbolId = args?["symbol_id"]?.GetValue<string>();

        // sym_ prefix → resolve stable_id to SymbolId first (bypasses name resolver).
        SymbolId symbolId;
        string symbolIdStr;
        if (!string.IsNullOrEmpty(explicitSymbolId) && explicitSymbolId.StartsWith("sym_", StringComparison.Ordinal))
        {
            var stableId = new StableId(explicitSymbolId);
            var stableResult = await _queryEngine.GetSymbolByStableIdAsync(routingResult.Value, stableId, ct).ConfigureAwait(false);
            if (stableResult.IsFailure) return HandlerHelpers.ErrWithNotFoundSuggestion(stableResult.Error, explicitSymbolId);
            symbolId = stableResult.Value.Data.SymbolId;
            symbolIdStr = symbolId.Value;
        }
        else
        {
            var resolved = await _resolver.ResolveAsync(args, routingResult.Value, ct).ConfigureAwait(false);
            if (resolved.Error is { } rErr) return Err(rErr);
            symbolId = resolved.Symbol!.Value;
            symbolIdStr = symbolId.Value;
        }

        var result = await _queryEngine.GetContextAsync(
            routingResult.Value, symbolId, calleeDepth, maxCallees, includeCode, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            if (!HasKnownPrefix(symbolIdStr))
            {
                var corrected = await TryAutoCorrectContextAsync(
                    symbolIdStr, routingResult.Value, calleeDepth, maxCallees, includeCode, ct).ConfigureAwait(false);
                if (corrected is not null) return corrected;
            }
            return HandlerHelpers.ErrWithNotFoundSuggestion(result.Error, symbolIdStr);
        }
        return Ok(result.Value);
    }

    // ── Symbol ID auto-correct ────────────────────────────────────────────────

    private static readonly string[] _idPrefixes = ["T:", "M:", "P:", "F:", "E:"];

    private static bool HasKnownPrefix(string id) =>
        id.StartsWith("sym_", StringComparison.Ordinal) ||
        _idPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal));

    private async Task<ToolCallResult?> TryAutoCorrectContextAsync(
        string rawId, RoutingContext routing, int calleeDepth, int maxCallees, bool includeCode, CancellationToken ct)
    {
        foreach (var prefix in _idPrefixes)
        {
            var candidateStr = prefix + rawId;
            var candidate = SymbolId.From(candidateStr);
            var result = await _queryEngine.GetContextAsync(
                routing, candidate, calleeDepth, maxCallees, includeCode, ct).ConfigureAwait(false);
            if (!result.IsSuccess) continue;

            var note = $"Note: auto-corrected symbol ID — added `{prefix}` prefix. " +
                       $"Use `{candidateStr}` in future calls for reliability.";
            var jsonNode = JsonNode.Parse(Ok(result.Value).Content)?.AsObject();
            if (jsonNode is not null && jsonNode.TryGetPropertyValue("answer", out var ans))
                jsonNode["answer"] = note + "\n\n" + (ans?.GetValue<string>() ?? "");
            return jsonNode is not null
                ? new ToolCallResult(jsonNode.ToJsonString())
                : Ok(result.Value);
        }
        return null;
    }

    // ── Shared routing ────────────────────────────────────────────────────────

    private async Task<Result<RoutingContext, ToolCallResult>> BuildRoutingResultAsync(
        string repoPath, JsonObject? args, CancellationToken ct)
    {
        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        var workspaceIdStr = HandlerHelpers.ResolveWorkspaceId(args, repoPath, _stickyRegistry);

        if (!string.IsNullOrEmpty(workspaceIdStr))
            return Result<RoutingContext, ToolCallResult>.Success(
                new RoutingContext(
                    repoId: repoId,
                    baselineCommitSha: sha,
                    workspaceId: WorkspaceId.From(workspaceIdStr),
                    consistency: ConsistencyMode.Workspace));

        return Result<RoutingContext, ToolCallResult>.Success(new RoutingContext(repoId: repoId, baselineCommitSha: sha));
    }

    // ── Schema helpers ────────────────────────────────────────────────────────

    private static JsonObject BuildSchema(string[] required, JsonObject properties) =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()),
            ["properties"] = properties,
        };

    private static JsonNode Prop(string type, string description) =>
        new JsonObject { ["type"] = type, ["description"] = description };

    private static ToolCallResult Ok<T>(T value) =>
        new(JsonSerializer.Serialize(value, CodeMapJsonOptions.Default));

    private static ToolCallResult Err(CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);

    private static ToolCallResult Err(ToolCallResult result) => result;

    private static ToolCallResult InvalidArg(string message) => Err(CodeMapError.InvalidArgument(message));

}
