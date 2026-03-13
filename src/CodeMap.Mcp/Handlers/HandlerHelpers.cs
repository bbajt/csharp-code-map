namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using CodeMap.Core.Errors;
using CodeMap.Mcp.Serialization;

/// <summary>
/// Shared static helpers used by multiple MCP tool handler classes.
/// Centralises NOT_FOUND suggestion logic and FQN parsing so changes
/// only need to be made in one place.
/// </summary>
internal static class HandlerHelpers
{
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
