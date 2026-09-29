namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Serialization;

/// <summary>
/// Shared static helpers used by multiple MCP tool handler classes.
/// Centralises NOT_FOUND suggestion logic, FQN parsing, and context-default
/// resolution so changes only need to be made in one place.
/// </summary>
internal static class HandlerHelpers
{
    // ── Exception → error-code classification (ADR-041) ───────────────────────

    private const int WinErrorSharingViolation = 32;
    private const int WinErrorLockViolation = 33;
    private const int EWouldBlockLinux = 11;
    private const int EWouldBlockMacOs = 35;

    /// <summary>
    /// Maps an exception caught by a handler's outer <c>catch</c> to an honest error:
    /// sharing violation → <c>WORKSPACE_IN_USE</c> (workspace-scoped) or <c>STORAGE_ERROR</c>;
    /// <see cref="IStorageFailure"/> / I/O / access denied → <c>STORAGE_ERROR</c> (retryable);
    /// <see cref="ArgumentException"/> (value-type guards on caller input) → <c>INVALID_ARGUMENT</c>;
    /// anything else → <c>INTERNAL_ERROR</c>. Never maps to <c>COMPILATION_FAILED</c>, and never
    /// labels an I/O conflict or a bug as <c>INVALID_ARGUMENT</c>.
    /// The original message is always kept. Callers still log the exception.
    /// </summary>
    /// <param name="ex">The caught exception (never <see cref="OperationCanceledException"/>).</param>
    /// <param name="operation">Tool name used as the message prefix, e.g. <c>workspace_create</c>.</param>
    /// <param name="workspaceId">Non-null for workspace-scoped operations.</param>
    internal static CodeMapError ClassifyException(Exception ex, string operation, string? workspaceId)
    {
        if (ex is IOException io && IsSharingViolation(io))
        {
            return workspaceId is not null
                ? CodeMapError.WorkspaceInUse(workspaceId, $"{operation}: {ex.Message}")
                : CodeMapError.StorageError($"{operation} failed: {ex.Message} In use by another process; retry shortly.");
        }

        return ex switch
        {
            // Value-type guards (WorkspaceId.From, FilePath.From, CommitSha.From) throw
            // ArgumentException for bad caller input inside the try — that IS a caller error.
            ArgumentException => CodeMapError.InvalidArgument($"{operation}: {ex.Message}"),
            IStorageFailure { IsTransient: true } => CodeMapError.StorageError($"{operation} failed: {ex.Message} Retry shortly."),
            IStorageFailure or IOException or UnauthorizedAccessException => CodeMapError.StorageError($"{operation} failed: {ex.Message}"),
            _ => CodeMapError.InternalError($"{operation} failed: {ex.Message}"),
        };
    }

    /// <summary>
    /// Classifies an exception that escaped a tool handler entirely (the <c>tools/call</c>
    /// boundary in <see cref="McpServer"/>, ADR-045). Same mapping as <see cref="ClassifyException"/>
    /// with one deliberate difference: <see cref="ArgumentException"/> → <c>INTERNAL_ERROR</c>.
    /// Handlers validate caller input themselves, so an argument exception that reaches the
    /// boundary is a defect (v2.6.2: <c>SymbolId.From("")</c> on an overlay symbol), not bad input.
    /// <c>INTERNAL_ERROR</c> carries <c>details.exception_type</c> for diagnosis.
    /// </summary>
    /// <param name="ex">The escaped exception (never <see cref="OperationCanceledException"/>).</param>
    /// <param name="tool">Tool name used as the message prefix.</param>
    /// <param name="workspaceId">The call's explicit <c>workspace_id</c>, if any (no sticky-default lookup).</param>
    internal static CodeMapError ClassifyUnhandled(Exception ex, string tool, string? workspaceId)
    {
        var error = ex is ArgumentException
            ? CodeMapError.InternalError($"{tool} failed: {ex.Message}")
            : ClassifyException(ex, tool, workspaceId);

        return error.Code == ErrorCodes.InternalError
            ? error with { Details = new Dictionary<string, object> { ["exception_type"] = ex.GetType().FullName ?? ex.GetType().Name } }
            : error;
    }

    /// <summary>Renders <paramref name="error"/> as the standard error-envelope tool result.</summary>
    internal static ToolCallResult ErrorResult(CodeMapError error) => Err(error);

    /// <summary>
    /// True when <paramref name="ex"/> is a file sharing/lock conflict with another process:
    /// Windows <c>ERROR_SHARING_VIOLATION</c> / <c>ERROR_LOCK_VIOLATION</c>, or on Unix the
    /// <c>EWOULDBLOCK</c> errno .NET reports when its advisory lock is held elsewhere.
    /// </summary>
    internal static bool IsSharingViolation(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            var code = ex.HResult & 0xFFFF;
            return (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
                && code is WinErrorSharingViolation or WinErrorLockViolation;
        }
        return ex.HResult == (OperatingSystem.IsMacOS() ? EWouldBlockMacOs : EWouldBlockLinux);
    }

    // ── Tool annotation presets (MCP 2025-03-26 spec) ─────────────────────────
    //
    // All CodeMap tools are closed-world (local index, no network), so OpenWorld
    // is implicit-false via ToolAnnotations's CodeMap-specific default. Every
    // mutating tool in CodeMap is observationally idempotent — ensure_baseline
    // returns "already_existed", refresh_overlay re-applies the same delta,
    // cleanup/remove_repo/delete are no-ops the second time, reset twice = reset
    // once. So Idempotent: true on every preset, including Destruct. This lets
    // clients retry transient failures without surfacing them as user-facing
    // errors. (This is the substantive correction over the spec's default for
    // write/destructive tools, which is conservative.)

    /// <summary>Read-only, idempotent, closed-world tool. Pure index reads.</summary>
    internal static readonly ToolAnnotations AnnotReadOnly =
        new(ReadOnly: true, Destructive: false, Idempotent: true);

    /// <summary>
    /// Write tool that creates or mutates local state but is not destructive
    /// (overlay deltas, baseline indexing). Idempotent: second call with same
    /// args is a no-op or returns the same result.
    /// </summary>
    internal static readonly ToolAnnotations AnnotWriteIdempotent =
        new(ReadOnly: false, Destructive: false, Idempotent: true);

    /// <summary>
    /// Destructive tool that irreversibly purges data (baselines, repos,
    /// workspaces, overlay revisions). Still idempotent: the resource is
    /// already gone the second time, so re-calling is a no-op.
    /// </summary>
    internal static readonly ToolAnnotations AnnotDestructIdempotent =
        new(ReadOnly: false, Destructive: true, Idempotent: true);

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
    /// <c>symbols_search</c> suggestion. For all other error codes
    /// returns a plain Err result unchanged.
    /// </summary>
    internal static ToolCallResult ErrWithNotFoundSuggestion(CodeMapError error, string symbolId)
    {
        if (error.Code != ErrorCodes.NotFound) return Err(error);
        var simpleName = ExtractSimpleName(symbolId);
        var enhanced = new CodeMapError(
            error.Code,
            error.Message + $" Tip: FQNs must be exact (Roslyn doc-comment ID format). " +
            $"Try: symbols_search(\"{simpleName}\") to find the correct symbol_id.");
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

    /// <summary>
    /// Async upgrade of <see cref="ErrWithNotFoundSuggestion"/> that actually runs
    /// the suggested search and inlines up to 3 candidate symbol IDs in the error.
    /// Agents reading the error see real IDs they can retry with — no second
    /// round-trip to symbols_search needed. Falls back to the text-only hint
    /// when the simple-name search itself returns nothing.
    /// </summary>
    internal static async Task<ToolCallResult> ErrWithFuzzyCandidatesAsync(
        CodeMapError error, string symbolId, IQueryEngine queryEngine,
        RoutingContext routing, CancellationToken ct)
    {
        if (error.Code != ErrorCodes.NotFound) return ErrWithNotFoundSuggestion(error, symbolId);

        var simpleName = ExtractSimpleName(symbolId);
        var budgets = new BudgetLimits(maxResults: 3);
        var searchResult = await queryEngine.SearchSymbolsAsync(routing, simpleName, null, budgets, ct).ConfigureAwait(false);
        if (searchResult.IsFailure || searchResult.Value.Data.Hits.Count == 0)
            return ErrWithNotFoundSuggestion(error, symbolId);

        var hits = searchResult.Value.Data.Hits;
        var lines = hits.Select(h => $"  {h.SymbolId.Value} — {h.Kind} ({h.FilePath.Value}:{h.Line})");
        var enhanced = new CodeMapError(error.Code, error.Message
            + $" The simple name '{simpleName}' matched {hits.Count} symbol(s) — "
            + "retry with one of these symbol_ids:\n" + string.Join("\n", lines));
        return Err(enhanced);
    }

    private static ToolCallResult Err(CodeMapError error) =>
        new(JsonSerializer.Serialize(error, CodeMapJsonOptions.Default), IsError: true);

    // ── Usage hints ───────────────────────────────────────────────────────────
    //
    // Agents that hit a dead-end response (empty result, unknown tool, opaque
    // success that doesn't match the agent's mental model) keep retrying or
    // burn turns asking the user what to do. The hints below ride along in the
    // existing `answer` field so unsophisticated clients see them as part of
    // the prose summary; richer clients can post-process.

    /// <summary>
    /// Builds a one-line hint for an empty <c>symbols_search</c> result. Tells the
    /// agent which adjacent tool to try next based on whether filters were
    /// applied — relaxation, then content fallback, then guide.
    /// </summary>
    internal static string EmptySearchHint(string? query, bool hasFilters)
    {
        if (hasFilters)
        {
            // The filters on symbols.search are top-level (`kinds` / `namespace` /
            // `file_path` / `project_name`), NOT nested under a `name_filter` object —
            // `name_filter` is the sibling tools' shape (get_card / get_context).
            // Keep the wording generic so the hint works for either set.
            return "0 hits. Tip: filters (kinds / namespace / file_path / project_name) narrow "
                + "the index — drop them and retry. If you want literal text in source bodies "
                + "(not symbol names), use code_search_text.";
        }
        if (string.IsNullOrEmpty(query))
        {
            return "0 hits. Tip: pass `kinds` (e.g. [\"Class\"]) to browse all symbols of a kind, "
                + "or `query` to search by name (words must all match; `A OR B` for alternatives).";
        }

        // PHASE-21-08: the v2 engine isn't FTS5. Terms already match by prefix, so suggesting
        // `query*` didn't help; for several words, every word must match, so suggest OR.
        var words = query.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"'))
            .Where(w => w.Length > 0 && w is not ("OR" or "AND"))
            .ToList();
        var orTip = words.Count > 1 && !query.Contains(" OR ", StringComparison.Ordinal)
            ? $"Every word must match; to accept any of them use `{string.Join(" OR ", words)}`. "
            : "";
        return "0 hits. Tip: " + orTip + "Terms match symbol names, namespaces and doc words by prefix "
            + "(`Order` finds `OrderService`), so try a shorter or different part of the name. "
            + "For literal text inside source bodies (string constants, comments) use code_search_text.";
    }

    /// <summary>
    /// Builds a hint for <c>symbols_get_card</c> / <c>get_context</c> on a Type
    /// symbol. The card returns class-level metadata + static-initializer callees,
    /// which agents often misread as "this Type has no members." Point them at the
    /// tools that actually enumerate members.
    /// </summary>
    internal static string? TypeCardHint(SymbolCard card)
    {
        if (card.Kind is not (SymbolKind.Class or SymbolKind.Interface
            or SymbolKind.Struct or SymbolKind.Record or SymbolKind.Enum))
        {
            return null;
        }
        if (card.FilePath.Value == "unknown") return null; // syntactic-fallback: file_path filter won't work
        var simpleName = ExtractSimpleName(card.SymbolId.Value);
        return $"This is a {card.Kind.ToString().ToLowerInvariant()}. Its card lists static-field-initializer "
            + $"callees only — NOT members. To enumerate members: "
            + $"symbols_search(kinds: [\"Method\",\"Property\",\"Field\"], file_path: \"{card.FilePath.Value}\"). "
            + $"For inheritance / interfaces / derived types use types_hierarchy(name: \"{simpleName}\").";
    }

    /// <summary>
    /// Picks the closest match for a misspelled tool or method name. Returns null
    /// when nothing is within Levenshtein distance 3 — better to stay silent than
    /// suggest something unrelated. Comparison is case-sensitive (callers should
    /// normalize first if needed).
    /// </summary>
    internal static string? ClosestName(string requested, IReadOnlyList<string> registered)
    {
        if (string.IsNullOrEmpty(requested) || registered.Count == 0) return null;
        var best = (Name: (string?)null, Dist: int.MaxValue);
        foreach (var name in registered)
        {
            var d = Levenshtein(requested, name);
            if (d < best.Dist) best = (name, d);
        }
        // Distance cap scales with the request length: a 5-char misspelling within
        // 2 is reasonable; a 30-char one within 4 still finds typos. Anything past
        // half the request length is noise, not a typo.
        var cap = Math.Min(4, Math.Max(2, requested.Length / 3));
        return best.Dist <= cap ? best.Name : null;
    }

    /// <summary>Iterative two-row Levenshtein distance — O(m·n) time, O(n) memory.</summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
