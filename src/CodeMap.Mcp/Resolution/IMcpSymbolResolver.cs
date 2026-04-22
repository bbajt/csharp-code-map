namespace CodeMap.Mcp.Resolution;

using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Turns the <c>symbol_id</c> / <c>name</c> / <c>name_filter</c> argument triad into a concrete
/// <see cref="SymbolId"/>. Lets every symbol-scoped MCP handler accept either an explicit
/// <c>symbol_id</c> (the precise form) or a <c>name</c> resolved via search (the sugar form).
/// </summary>
public interface IMcpSymbolResolver
{
    /// <summary>
    /// Resolves a handler argument bundle into a <see cref="SymbolId"/>.
    /// <list type="bullet">
    ///   <item>Explicit <c>symbol_id</c> → returned verbatim; no search, no ambiguity check.</item>
    ///   <item>Non-empty <c>name</c> → resolved via <c>symbols.search</c>, optionally narrowed by <c>name_filter</c>.</item>
    ///   <item>Zero matches → <c>NOT_FOUND</c>.</item>
    ///   <item>Exactly one match → that symbol's ID.</item>
    ///   <item>Two or more matches → <c>AMBIGUOUS</c> with up to <c>MaxCandidates</c> candidate IDs in the message.</item>
    ///   <item>Neither <c>symbol_id</c> nor <c>name</c> provided → <c>INVALID_ARGUMENT</c>.</item>
    /// </list>
    /// </summary>
    Task<ResolveResult> ResolveAsync(JsonObject? args, RoutingContext routing, CancellationToken ct);
}

/// <summary>
/// Outcome of an <see cref="IMcpSymbolResolver.ResolveAsync"/> call. Exactly one of
/// <see cref="Symbol"/> or <see cref="Error"/> is non-null.
/// </summary>
public readonly record struct ResolveResult(SymbolId? Symbol, CodeMapError? Error)
{
    /// <summary>True when resolution produced a concrete symbol.</summary>
    public bool IsSuccess => Symbol is not null;
}
