namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Serialization;

/// <summary>
/// Shared static helpers used by multiple MCP tool handler classes.
/// Centralises NOT_FOUND suggestion logic, FQN parsing, and context-default
/// resolution so changes only need to be made in one place.
/// </summary>
internal static class HandlerHelpers
{
    /// <summary>
    /// Resolves the <c>repo_path</c> argument into a concrete path, using the registry
    /// to auto-default single-repo sessions.
    /// <list type="bullet">
    ///   <item>Non-empty explicit <c>repo_path</c> → returned verbatim (normalized).</item>
    ///   <item>Omitted and exactly one repo registered → that repo.</item>
    ///   <item>Omitted and 0 / 2+ repos registered → <see cref="ToolCallResult"/> error.</item>
    /// </list>
    /// </summary>
    internal static (string? RepoPath, ToolCallResult? Error) ResolveRepoPath(
        JsonObject? args, IRepoRegistry registry)
    {
        var explicitPath = args?["repo_path"]?.GetValue<string>();
        var resolved = registry.Resolve(explicitPath);
        return resolved.Error is { } err
            ? ((string?)null, (ToolCallResult?)Err(err))
            : (resolved.RepoPath, null);
    }

    /// <summary>
    /// Resolves the <c>workspace_id</c> argument, falling back to the sticky default
    /// for the given repo when the key is absent. Explicit empty string
    /// (<c>"workspace_id": ""</c>) is treated as "committed mode" and does NOT fall
    /// back to sticky — lets callers opt out of the default explicitly.
    /// </summary>
    internal static string? ResolveWorkspaceId(
        JsonObject? args, string repoPath, IWorkspaceStickyRegistry sticky)
    {
        var node = args?["workspace_id"];
        if (node is not null)
            return node.GetValue<string>();
        return sticky.Get(repoPath);
    }
    /// <summary>
    /// Returns an Err result for NOT_FOUND errors augmented with a
    /// <c>symbols.search</c> suggestion. For all other error codes
    /// returns a plain Err result unchanged.
    /// </summary>
    internal static ToolCallResult ErrWithNotFoundSuggestion(CodeMapError error, string symbolId)
    {
        if (error.Code != ErrorCodes.NotFound) return Err(error);
        var simpleName = ExtractSimpleName(symbolId);
        var enhanced = new CodeMapError(
            error.Code,
            error.Message + $" Tip: FQNs must be exact (Roslyn doc-comment ID format). " +
            $"Try: symbols.search(\"{simpleName}\") to find the correct symbol_id.");
        return Err(enhanced);
    }

    /// <summary>
    /// Extracts the simple member name from a FQN or sym_ stable ID.
    /// <list type="bullet">
    ///   <item>"M:Namespace.Class.Method(params)" → "Method"</item>
    ///   <item>"T:Namespace.Class"               → "Class"</item>
    ///   <item>"sym_abc123"                       → "sym_abc123" (returned as-is)</item>
    /// </list>
    /// </summary>
    internal static string ExtractSimpleName(string symbolId)
    {
        var withoutPrefix = symbolId.Length > 2 && symbolId[1] == ':'
            ? symbolId[2..] : symbolId;
        var parenIdx = withoutPrefix.IndexOf('(', StringComparison.Ordinal);
        var name = parenIdx >= 0 ? withoutPrefix[..parenIdx] : withoutPrefix;
        var dotIdx = name.LastIndexOf('.');
        return dotIdx >= 0 ? name[(dotIdx + 1)..] : name;
    }

    private static ToolCallResult Err(CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);
}
