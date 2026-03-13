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
/// Handles the <c>index.diff</c> MCP tool.
/// </summary>
/// <remarks>
/// <b>index.diff</b> params: repo_path, from_commit, to_commit (all required).
/// Optional: kinds (symbol kind filter), include_facts (default true).
/// Both commits must have existing baselines — returns INDEX_NOT_AVAILABLE otherwise.
/// Use <c>to_commit: "HEAD"</c> to diff against the current working commit.
/// </remarks>
public sealed class DiffHandler
{
    private readonly IQueryEngine _queryEngine;
    private readonly IGitService _gitService;
    private readonly ISymbolStore _symbolStore;
    private readonly ILogger<DiffHandler> _logger;

    public DiffHandler(
        IQueryEngine queryEngine,
        IGitService gitService,
        ISymbolStore symbolStore,
        ILogger<DiffHandler> logger)
    {
        _queryEngine = queryEngine;
        _gitService = gitService;
        _symbolStore = symbolStore;
        _logger = logger;
    }

    /// <summary>Registers index.diff into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "index.diff",
            "Compare two indexed commits and show what changed semantically: symbols added/removed/renamed, endpoints added/removed, config keys and DI registrations changed.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray
                {
                    JsonValue.Create("repo_path"),
                    JsonValue.Create("from_commit"),
                    JsonValue.Create("to_commit"),
                },
                ["properties"] = new JsonObject
                {
                    ["repo_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Absolute path to the repository root",
                    },
                    ["from_commit"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Base commit SHA (the 'before'). Must have an existing baseline.",
                    },
                    ["to_commit"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Target commit SHA (the 'after'). Use 'HEAD' for the current commit.",
                    },
                    ["kinds"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Optional: filter symbol changes to specific kinds (e.g. Class, Method, Interface). Default: all.",
                    },
                    ["include_facts"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include fact-level diffs (endpoints, config, DB tables, DI). Default: true.",
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

        var fromCommitStr = args?["from_commit"]?.GetValue<string>();
        if (string.IsNullOrEmpty(fromCommitStr))
            return Err(CodeMapError.InvalidArgument("from_commit is required"));

        var toCommitStr = args?["to_commit"]?.GetValue<string>();
        if (string.IsNullOrEmpty(toCommitStr))
            return Err(CodeMapError.InvalidArgument("to_commit is required"));

        var includeFacts = args?["include_facts"]?.GetValue<bool>() ?? true;

        // Resolve HEAD
        if (toCommitStr.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            toCommitStr = (await _gitService.GetCurrentCommitAsync(repoPath, ct)).Value;
        if (fromCommitStr.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            fromCommitStr = (await _gitService.GetCurrentCommitAsync(repoPath, ct)).Value;

        CommitSha fromCommit, toCommit;
        try
        {
            fromCommit = CommitSha.From(fromCommitStr);
            toCommit   = CommitSha.From(toCommitStr);
        }
        catch (ArgumentException ex)
        {
            return Err(CodeMapError.InvalidArgument($"Invalid commit SHA: {ex.Message}"));
        }

        var repoId = await _gitService.GetRepoIdentityAsync(repoPath, ct);

        // Verify both baselines exist before invoking the differ
        if (!await _symbolStore.BaselineExistsAsync(repoId, fromCommit, ct))
            return Err(CodeMapError.IndexNotAvailable(repoId.Value, fromCommitStr));

        if (!await _symbolStore.BaselineExistsAsync(repoId, toCommit, ct))
            return Err(CodeMapError.IndexNotAvailable(repoId.Value, toCommitStr));

        // Parse optional kinds filter
        IReadOnlyList<SymbolKind>? kinds = null;
        if (args?["kinds"] is JsonArray kindsArr)
        {
            var parsed = kindsArr
                .Select(n => n?.GetValue<string>())
                .Where(s => s is not null && Enum.TryParse<SymbolKind>(s, true, out _))
                .Select(s => Enum.Parse<SymbolKind>(s!, true))
                .ToList();
            if (parsed.Count > 0) kinds = parsed;
        }

        var routing = new RoutingContext(repoId: repoId, baselineCommitSha: toCommit);
        var result  = await _queryEngine.DiffAsync(routing, fromCommit, toCommit, kinds, includeFacts, ct);

        if (result.IsFailure)
            return Err(result.Error);

        // Return the rendered markdown as the tool content
        return new ToolCallResult(result.Value.Data.Markdown);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ToolCallResult Ok<T>(T value) =>
        new(JsonSerializer.Serialize(value, CodeMapJsonOptions.Default));

    private static ToolCallResult Err(CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);
}
