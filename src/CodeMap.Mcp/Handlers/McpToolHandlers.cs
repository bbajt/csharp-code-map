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
/// Handles the four query-based MCP tools:
///   symbols.search, symbols.get_card, code.get_span, symbols.get_definition_span
/// </summary>
/// <remarks>
/// <b>symbols.search</b> params: repo_path, query (required); workspace_id, virtual_files, kinds, namespace, file_path, limit (optional).
/// limit is clamped to [1, 100]; default 20. kinds filters by <see cref="CodeMap.Core.Enums.SymbolKind"/>.
///
/// <b>symbols.get_card</b> params: repo_path, symbol_id (required); workspace_id, virtual_files (optional).
/// symbol_id with "sym_" prefix dispatches to stable-ID lookup instead of FQN lookup.
///
/// <b>code.get_span</b> params: repo_path, file_path, start_line, end_line (required);
/// workspace_id, virtual_files, context_lines, max_lines (optional).
/// max_lines clamped to [1, 400]; default 120.
///
/// <b>symbols.get_definition_span</b> params: repo_path, symbol_id (required);
/// workspace_id, virtual_files, max_lines, context_lines (optional).
///
/// All tools support Committed, Workspace, and Ephemeral consistency modes via
/// <see cref="BuildRoutingResultAsync"/>. Ephemeral mode requires both workspace_id
/// and virtual_files. Returns INVALID_ARGUMENT if required params are missing.
/// </remarks>
public sealed class McpToolHandlers
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly IMcpSymbolResolver _resolver;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _stickyRegistry;
    private readonly ILogger<McpToolHandlers> _logger;

    public McpToolHandlers(
        IQueryEngine queryEngine,
        IGitService gitService,
        IMcpSymbolResolver resolver,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry stickyRegistry,
        ILogger<McpToolHandlers> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _resolver = resolver;
        _repoRegistry = repoRegistry;
        _stickyRegistry = stickyRegistry;
        _logger = logger;
    }

    /// <summary>Registers the 4 query-based tools into the ToolRegistry.</summary>
    public void RegisterQueryTools(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "symbols.search",
            "Search for C# symbols by name, namespace, kind, or file path using full-text search.",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root. Optional when exactly one repo is indexed in this session."),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay data. Falls back to the sticky workspace set by the most recent workspace.create."),
                    ["virtual_files"] = VirtualFilesProp(),
                    ["query"] = Prop("string", "FTS5 search query (optional when kinds is set). Omit to browse all symbols of the specified kinds. Space = implicit AND. Use OR for alternatives: 'Foo OR Bar'. Use * for prefix matching: 'Order*'."),
                    ["kinds"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Filter by SymbolKind (e.g. [\"Class\", \"Method\"]). When query is omitted, kinds is required — returns all symbols of those types.",
                    },
                    ["namespace"] = Prop("string", "Namespace prefix filter (case-insensitive)."),
                    ["file_path"] = Prop("string", "File path prefix filter. E.g., 'src/' for production code only, 'tests/' for test code only."),
                    ["project_name"] = Prop("string", "Exact project name filter (case-insensitive). E.g., 'CodeMap.Core'."),
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20, max: 100)" },
                }),
            HandleSearchAsync));

        registry.Register(new ToolDefinition(
            "symbols.get_card",
            "Get a full structured summary of a C# symbol including signature, docs, facts, and source code. Accepts either symbol_id (exact) or name (resolved via search).",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay data"),
                    ["virtual_files"] = VirtualFilesProp(),
                    ["symbol_id"] = Prop("string", "Fully qualified symbol ID or sym_ stable ID. Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = NameProp(),
                    ["name_filter"] = NameFilterProp(),
                    ["include_code"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include source code in response (default: true). When true, the symbol's full source is included up to 100 lines; methods are rarely truncated. Set false for metadata-only lookups (faster, no disk read)." },
                }),
            HandleGetCardAsync));

        registry.Register(new ToolDefinition(
            "code.get_span",
            "Read a bounded excerpt of source code with line numbers.",
            BuildSchema(
                required: ["file_path", "start_line", "end_line"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay data"),
                    ["virtual_files"] = VirtualFilesProp(),
                    ["file_path"] = Prop("string", "Repo-relative file path"),
                    ["start_line"] = new JsonObject { ["type"] = "integer" },
                    ["end_line"] = new JsonObject { ["type"] = "integer" },
                    ["context_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Extra lines before/after (default: 0)" },
                    ["max_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Budget cap (default: 120)" },
                }),
            HandleGetSpanAsync));

        registry.Register(new ToolDefinition(
            "symbols.get_definition_span",
            "Source code only — no card metadata or fact hydration. Accepts either symbol_id (exact) or name (resolved via search). Use for batch reads or when you need precise line control. For most uses, prefer symbols.get_card which includes source automatically.",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay data"),
                    ["virtual_files"] = VirtualFilesProp(),
                    ["symbol_id"] = Prop("string", "Fully qualified symbol ID. Provide either symbol_id (exact) or name (resolved)."),
                    ["name"] = NameProp(),
                    ["name_filter"] = NameFilterProp(),
                    ["max_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Max lines to return (default: 120)" },
                    ["context_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Context around definition (default: 2)" },
                }),
            HandleGetDefinitionSpanAsync));

        registry.Register(new ToolDefinition(
            "code.search_text",
            "Search indexed source file content by regex pattern. Returns file:line:excerpt for each match. Searches only indexed files (no bin/obj). Use file_path to restrict to a subtree.",
            BuildSchema(
                required: ["pattern"],
                properties: new JsonObject
                {
                    ["repo_path"] = Prop("string", "Absolute path to the repository root"),
                    ["workspace_id"] = Prop("string", "Optional: workspace ID for overlay data"),
                    ["pattern"] = Prop("string",
                        "Regular expression to search for (line-by-line). Example: 'OrderService\\b' or 'TODO:'"),
                    ["file_path"] = Prop("string",
                        "File path prefix filter (e.g. 'src/' for production files only, 'tests/' for test files)."),
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max matches to return (default: 50, max: 200)" },
                }),
            HandleSearchTextAsync));
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    internal async Task<ToolCallResult> HandleSearchAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        // Treat bare "*" as no query — engine routes to kinds-browse path
        var query = args?["query"]?.GetValue<string>();
        if (query == "*") query = null;

        var routingResult = await BuildRoutingResultAsync(repoPath!, args, ct).ConfigureAwait(false);
        if (routingResult.IsFailure) return routingResult.Error;

        var limit = args.GetInt("limit", 20);
        var filters = BuildSearchFilters(args);
        var budgets = new BudgetLimits(maxResults: Math.Clamp(limit, 1, 100));

        var result = await _queryEngine.SearchSymbolsAsync(routingResult.Value, query, filters, budgets, ct).ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    internal async Task<ToolCallResult> HandleGetCardAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var includeCode = args?["include_code"]?.GetValue<bool>() ?? true;

        var routingResult = await BuildRoutingResultAsync(repoPath!, args, ct).ConfigureAwait(false);
        if (routingResult.IsFailure) return routingResult.Error;

        var explicitSymbolId = args?["symbol_id"]?.GetValue<string>();

        // sym_ prefix → stable_id lookup (stable symbol identity). Kept on the explicit
        // symbol_id path — sym_ IDs never come from name resolution.
        if (!string.IsNullOrEmpty(explicitSymbolId) && explicitSymbolId.StartsWith("sym_", StringComparison.Ordinal))
        {
            var stableId = new StableId(explicitSymbolId);
            var stableResult = await _queryEngine.GetSymbolByStableIdAsync(routingResult.Value, stableId, ct).ConfigureAwait(false);
            if (stableResult.IsFailure) return HandlerHelpers.ErrWithNotFoundSuggestion(stableResult.Error, explicitSymbolId);
            return await AppendSourceCodeAsync(stableResult.Value.Data.SymbolId, stableResult.Value, routingResult.Value, includeCode, ct).ConfigureAwait(false);
        }

        var resolved = await _resolver.ResolveAsync(args, routingResult.Value, ct).ConfigureAwait(false);
        if (resolved.Error is { } rErr) return Err(rErr);
        var symbolId = resolved.Symbol!.Value;
        var symbolIdStr = symbolId.Value;

        var result = await _queryEngine.GetSymbolCardAsync(routingResult.Value, symbolId, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            if (!HasKnownPrefix(symbolIdStr))
            {
                var corrected = await TryAutoCorrectCardAsync(symbolIdStr, routingResult.Value, includeCode, ct).ConfigureAwait(false);
                if (corrected is not null) return corrected;
            }
            return HandlerHelpers.ErrWithNotFoundSuggestion(result.Error, symbolIdStr);
        }
        return await AppendSourceCodeAsync(symbolId, result.Value, routingResult.Value, includeCode, ct).ConfigureAwait(false);
    }

    // ── Symbol ID auto-correct ────────────────────────────────────────────────

    private static readonly string[] _idPrefixes = ["T:", "M:", "P:", "F:", "E:"];

    /// <summary>Returns true if the ID already has a recognized Roslyn doc-comment prefix or sym_ prefix.</summary>
    private static bool HasKnownPrefix(string id) =>
        id.StartsWith("sym_", StringComparison.Ordinal) ||
        _idPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Tries prefixing the raw symbol ID with T:, M:, P:, F:, E: in order.
    /// Returns the first successful card result with a guidance note, or null if all fail.
    /// </summary>
    private async Task<ToolCallResult?> TryAutoCorrectCardAsync(
        string rawId, RoutingContext routing, bool includeCode, CancellationToken ct)
    {
        foreach (var prefix in _idPrefixes)
        {
            var candidateStr = prefix + rawId;
            var candidate = SymbolId.From(candidateStr);
            var result = await _queryEngine.GetSymbolCardAsync(routing, candidate, ct).ConfigureAwait(false);
            if (!result.IsSuccess) continue;

            var toolResult = await AppendSourceCodeAsync(candidate, result.Value, routing, includeCode, ct).ConfigureAwait(false);

            var note = $"Note: auto-corrected symbol ID — added `{prefix}` prefix. " +
                       $"Use `{candidateStr}` in future calls for reliability.";
            return InjectAnswerNote(toolResult, note);
        }
        return null;
    }

    /// <summary>Prepends a note to the answer field in a serialized ResponseEnvelope JSON.</summary>
    private static ToolCallResult InjectAnswerNote(ToolCallResult result, string note)
    {
        var jsonNode = JsonNode.Parse(result.Content)?.AsObject();
        if (jsonNode is null) return result;
        if (jsonNode.TryGetPropertyValue("answer", out var ans))
            jsonNode["answer"] = note + "\n\n" + (ans?.GetValue<string>() ?? "");
        return new ToolCallResult(jsonNode.ToJsonString());
    }

    /// <summary>
    /// Injects <c>source</c>, optional <c>note</c>, and optional <c>source_code</c> into
    /// the serialized card response without mutating <see cref="SymbolCard"/>.
    /// Always injects a <c>source</c> discriminator ("source_code", "metadata_stub", or "decompiled").
    /// </summary>
    private async Task<ToolCallResult> AppendSourceCodeAsync(
        SymbolId symbolId,
        ResponseEnvelope<SymbolCard> envelope,
        RoutingContext routing,
        bool includeCode,
        CancellationToken ct)
    {
        var card = envelope.Data;
        const int MaxCodeLines = 100;

        // Serialize base envelope and get the data node for injection
        var rawJson = JsonSerializer.Serialize(envelope, CodeMapJsonOptions.Default);
        var jsonNode = JsonNode.Parse(rawJson)!.AsObject();
        if (!jsonNode.TryGetPropertyValue("data", out var dn) || dn is not JsonObject dataObj)
            return new ToolCallResult(rawJson);

        // Always inject source discriminator
        string sourceValue = card.IsDecompiled switch
        {
            2 => "decompiled",
            1 => "metadata_stub",
            _ => "source_code"
        };
        dataObj["source"] = sourceValue;

        if (!includeCode)
            return new ToolCallResult(jsonNode.ToJsonString());

        if (card.IsDecompiled == 2)
        {
            // Decompiled symbol: read the entire virtual file
            if (!string.IsNullOrEmpty(card.FilePath.Value))
            {
                var spanResult = await _queryEngine.GetSpanAsync(
                    routing, card.FilePath, 1, int.MaxValue, 0,
                    new BudgetLimits(maxLines: MaxCodeLines), ct).ConfigureAwait(false);
                if (spanResult.IsSuccess)
                {
                    dataObj["source_code"] = spanResult.Value.Data.Content;
                    if (spanResult.Value.Data.Truncated)
                        dataObj["code_truncated"] = true;
                }
            }
        }
        else if (card.IsDecompiled == 1)
        {
            // Decompilation was attempted (include_code=true) but is_decompiled still 1 → failed
            dataObj["note"] =
                "Source code could not be reconstructed for this type. " +
                "The metadata stub (signature + XML doc) is available above.";
        }
        else if (card.SpanStart > 0 && card.SpanEnd > 0)
        {
            // Normal source symbol: read from disk via definition span
            var spanResult = await _queryEngine.GetDefinitionSpanAsync(
                routing, symbolId, maxLines: MaxCodeLines, contextLines: 0, ct).ConfigureAwait(false);
            if (spanResult.IsSuccess)
            {
                var spanData = spanResult.Value.Data;
                var sourceCode = spanData.Content;
                if (spanData.Truncated)
                {
                    var remaining = card.SpanEnd - card.SpanStart - MaxCodeLines;
                    sourceCode += $"\n// ... ({remaining} more lines — use symbols.get_definition_span for full source)";
                }
                dataObj["source_code"] = sourceCode;
                if (spanData.Truncated)
                    dataObj["code_truncated"] = true;
            }
        }

        return new ToolCallResult(jsonNode.ToJsonString());
    }

    internal async Task<ToolCallResult> HandleGetSpanAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;
        var filePathStr = args?["file_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(filePathStr)) return InvalidArg("file_path is required");
        if (filePathStr == "unknown")
            return InvalidArg("File path is 'unknown' — this symbol has no source location (metadata or decompiled assembly). Use symbols.get_card with include_code=true instead.");

        int startLine = args.GetInt("start_line", 0);
        int endLine = args.GetInt("end_line", 0);
        if (startLine <= 0) return InvalidArg("start_line must be a positive integer");
        if (endLine < startLine) return InvalidArg("end_line must be >= start_line");

        var contextLines = args.GetInt("context_lines", 0);
        var maxLines = args.GetInt("max_lines", 120);
        var budgets = new BudgetLimits(maxLines: Math.Clamp(maxLines, 1, 400));

        var routingResult = await BuildRoutingResultAsync(repoPath!, args, ct).ConfigureAwait(false);
        if (routingResult.IsFailure) return routingResult.Error;

        var filePath = FilePath.From(filePathStr);
        var result = await _queryEngine.GetSpanAsync(routingResult.Value, filePath, startLine, endLine, contextLines, budgets, ct).ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    internal async Task<ToolCallResult> HandleGetDefinitionSpanAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        var maxLines = args.GetInt("max_lines", 120);
        var contextLines = args.GetInt("context_lines", 2);

        var routingResult = await BuildRoutingResultAsync(repoPath!, args, ct).ConfigureAwait(false);
        if (routingResult.IsFailure) return routingResult.Error;

        var resolved = await _resolver.ResolveAsync(args, routingResult.Value, ct).ConfigureAwait(false);
        if (resolved.Error is { } rErr) return Err(rErr);
        var symbolId = resolved.Symbol!.Value;

        var result = await _queryEngine.GetDefinitionSpanAsync(routingResult.Value, symbolId, maxLines, contextLines, ct).ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    internal async Task<ToolCallResult> HandleSearchTextAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;
        var pattern = args?["pattern"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pattern)) return InvalidArg("pattern is required");

        var routingResult = await BuildRoutingResultAsync(repoPath!, args, ct).ConfigureAwait(false);
        if (routingResult.IsFailure) return routingResult.Error;

        var limit = args.GetInt("limit", 50);
        var filePathFilter = args?["file_path"]?.GetValue<string>();
        var budgets = new BudgetLimits(maxResults: Math.Clamp(limit, 1, 200));

        var result = await _queryEngine.SearchTextAsync(routingResult.Value, pattern, filePathFilter, budgets, ct).ConfigureAwait(false);
        return result.Match(Ok, Err);
    }

    // ── Shared routing helper ─────────────────────────────────────────────────

    internal async Task<Result<RoutingContext, ToolCallResult>> BuildRoutingResultAsync(
        string repoPath, JsonObject? args, CancellationToken ct)
    {
        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);

        CommitSha sha;
        var commitShaStr = args?["commit_sha"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(commitShaStr))
            sha = CommitSha.From(commitShaStr);
        else
            sha = await _gitService.GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);

        // Parse virtual_files
        var virtualFilesNode = args?["virtual_files"] as JsonArray;
        List<VirtualFile>? virtualFiles = null;
        if (virtualFilesNode is not null && virtualFilesNode.Count > 0)
        {
            virtualFiles = [];
            foreach (var node in virtualFilesNode)
            {
                if (node is not JsonObject fileObj) continue;
                var fp = fileObj["file_path"]?.GetValue<string>();
                var content = fileObj["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(fp) && content is not null)
                    virtualFiles.Add(new VirtualFile(FilePath.From(fp), content));
            }
        }

        // Read workspace_id with sticky-default fallback. Empty string (explicit opt-out)
        // is passed through so the committed-mode branch below still fires.
        var workspaceIdStr = HandlerHelpers.ResolveWorkspaceId(args, repoPath, _stickyRegistry);

        // Ephemeral mode: virtual files present
        if (virtualFiles is { Count: > 0 })
        {
            if (string.IsNullOrEmpty(workspaceIdStr))
                return Result<RoutingContext, ToolCallResult>.Failure(
                    InvalidArg("Ephemeral mode requires workspace_id when virtual_files are provided"));

            var totalChars = virtualFiles.Sum(vf => vf.Content.Length);
            if (totalChars > BudgetLimits.HardCaps.MaxChars)
                return Result<RoutingContext, ToolCallResult>.Failure(
                    Err(new CodeMapError(ErrorCodes.BudgetExceeded,
                        $"Virtual files total {totalChars} chars exceeds limit {BudgetLimits.HardCaps.MaxChars}")));

            var workspaceId = WorkspaceId.From(workspaceIdStr);
            return Result<RoutingContext, ToolCallResult>.Success(new RoutingContext(
                repoId: repoId,
                workspaceId: workspaceId,
                consistency: ConsistencyMode.Ephemeral,
                baselineCommitSha: sha,
                virtualFiles: virtualFiles));
        }

        // Workspace mode: workspace_id present
        if (!string.IsNullOrEmpty(workspaceIdStr))
        {
            var workspaceId = WorkspaceId.From(workspaceIdStr);
            return Result<RoutingContext, ToolCallResult>.Success(new RoutingContext(
                repoId: repoId,
                workspaceId: workspaceId,
                consistency: ConsistencyMode.Workspace,
                baselineCommitSha: sha));
        }

        // Committed mode
        return Result<RoutingContext, ToolCallResult>.Success(
            new RoutingContext(repoId: repoId, baselineCommitSha: sha));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolSearchFilters? BuildSearchFilters(JsonObject? args)
    {
        var kindsNode = args?["kinds"] as JsonArray;
        List<SymbolKind>? kinds = null;
        if (kindsNode is not null)
        {
            kinds = [];
            foreach (var node in kindsNode)
            {
                var kindStr = node?.GetValue<string>();
                if (kindStr is not null && Enum.TryParse<SymbolKind>(kindStr, ignoreCase: true, out var k))
                    kinds.Add(k);
            }
        }

        var ns = args?["namespace"]?.GetValue<string>();
        var filePath = args?["file_path"]?.GetValue<string>();
        var projectName = args?["project_name"]?.GetValue<string>();
        if (kinds is null && ns is null && filePath is null && projectName is null) return null;
        return new SymbolSearchFilters(Kinds: kinds?.AsReadOnly(), Namespace: ns, FilePath: filePath, ProjectName: projectName);
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

    /// <summary>
    /// Shared <c>name</c> schema property for symbol-scoped tools. Keeps the description
    /// consistent so agents learn one rule: "either symbol_id or name".
    /// </summary>
    internal static JsonObject NameProp() => Prop("string",
        "Symbol name (class, method, property). Resolved via search on the server. " +
        "Use this shorthand when you don't already have a symbol_id. " +
        "On 2+ matches the error lists candidates — pass one as symbol_id or narrow with name_filter.");

    /// <summary>
    /// Shared <c>name_filter</c> schema property — same shape as symbols.search filters.
    /// Lets callers disambiguate without resorting to a separate search call.
    /// </summary>
    internal static JsonObject NameFilterProp() => new()
    {
        ["type"] = "object",
        ["description"] = "Optional scope for name resolution. Narrows matches when a name is ambiguous.",
        ["properties"] = new JsonObject
        {
            ["namespace"] = Prop("string", "Namespace prefix filter (case-insensitive)."),
            ["file_path"] = Prop("string", "File path prefix filter, e.g. 'src/'."),
            ["project_name"] = Prop("string", "Exact project name filter (case-insensitive)."),
            ["kinds"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Restrict to these SymbolKinds, e.g. [\"Method\"]."
            },
        },
    };

    private static JsonObject VirtualFilesProp() => new()
    {
        ["type"] = "array",
        ["description"] = "Optional unsaved file contents for Ephemeral mode (requires workspace_id)",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["file_path"] = Prop("string", "Repo-relative file path"),
                ["content"] = Prop("string", "File content (UTF-8)"),
            },
        },
    };
}
