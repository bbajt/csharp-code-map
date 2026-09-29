namespace CodeMap.Core.Models;

/// <summary>
/// MCP tool names. Canonical names are safe for MCP clients and LLM APIs (<c>^[a-zA-Z0-9_-]{1,64}$</c>,
/// ADR-051). Every tool name CodeMap emits (registrations, next_actions, hints, errors, guide) comes
/// from here. Lives in Core because the Query layer emits next_actions.
/// </summary>
public static class ToolNames
{
    /// <summary><c>repo_status</c>: index health for a repo.</summary>
    public const string RepoStatus = "repo_status";
    /// <summary><c>index_ensure_baseline</c>: build or verify the baseline index.</summary>
    public const string IndexEnsureBaseline = "index_ensure_baseline";
    /// <summary><c>index_refresh_overlay</c>: re-index edited files into the workspace overlay.</summary>
    public const string IndexRefreshOverlay = "index_refresh_overlay";
    /// <summary><c>index_diff</c>: semantic diff between two commits.</summary>
    public const string IndexDiff = "index_diff";
    /// <summary><c>index_list_baselines</c>: list cached baselines.</summary>
    public const string IndexListBaselines = "index_list_baselines";
    /// <summary><c>index_cleanup</c>: remove stale baselines.</summary>
    public const string IndexCleanup = "index_cleanup";
    /// <summary><c>index_remove_repo</c>: remove every baseline of a repo.</summary>
    public const string IndexRemoveRepo = "index_remove_repo";
    /// <summary><c>workspace_create</c>: create a workspace overlay.</summary>
    public const string WorkspaceCreate = "workspace_create";
    /// <summary><c>workspace_reset</c>: reset a workspace overlay to its baseline.</summary>
    public const string WorkspaceReset = "workspace_reset";
    /// <summary><c>workspace_list</c>: list workspaces.</summary>
    public const string WorkspaceList = "workspace_list";
    /// <summary><c>workspace_delete</c>: delete a workspace.</summary>
    public const string WorkspaceDelete = "workspace_delete";
    /// <summary><c>symbols_search</c>: find symbols by name.</summary>
    public const string SymbolsSearch = "symbols_search";
    /// <summary><c>symbols_get_card</c>: a symbol's metadata, facts and source.</summary>
    public const string SymbolsGetCard = "symbols_get_card";
    /// <summary><c>symbols_get_context</c>: card + source + callee cards.</summary>
    public const string SymbolsGetContext = "symbols_get_context";
    /// <summary><c>symbols_get_definition_span</c>: a symbol's source span.</summary>
    public const string SymbolsGetDefinitionSpan = "symbols_get_definition_span";
    /// <summary><c>code_get_span</c>: read a range of source lines.</summary>
    public const string CodeGetSpan = "code_get_span";
    /// <summary><c>code_search_text</c>: regex search over indexed source.</summary>
    public const string CodeSearchText = "code_search_text";
    /// <summary><c>refs_find</c>: references to a symbol.</summary>
    public const string RefsFind = "refs_find";
    /// <summary><c>graph_callers</c>: callers of a symbol.</summary>
    public const string GraphCallers = "graph_callers";
    /// <summary><c>graph_callees</c>: callees of a symbol.</summary>
    public const string GraphCallees = "graph_callees";
    /// <summary><c>graph_trace_feature</c>: annotated feature flow from an entry point.</summary>
    public const string GraphTraceFeature = "graph_trace_feature";
    /// <summary><c>types_hierarchy</c>: base types, interfaces and derived types.</summary>
    public const string TypesHierarchy = "types_hierarchy";
    /// <summary><c>codemap_summarize</c>: codebase overview.</summary>
    public const string CodemapSummarize = "codemap_summarize";
    /// <summary><c>codemap_export</c>: portable context dump.</summary>
    public const string CodemapExport = "codemap_export";
    /// <summary><c>codemap_guide</c>: quick-start guide.</summary>
    public const string CodemapGuide = "codemap_guide";
    /// <summary><c>surfaces_list_endpoints</c>: HTTP endpoints.</summary>
    public const string SurfacesListEndpoints = "surfaces_list_endpoints";
    /// <summary><c>surfaces_list_config_keys</c>: configuration key usages.</summary>
    public const string SurfacesListConfigKeys = "surfaces_list_config_keys";
    /// <summary><c>surfaces_list_db_tables</c>: database tables.</summary>
    public const string SurfacesListDbTables = "surfaces_list_db_tables";

    /// <summary>Version in which the dotted aliases may be removed; quoted in the deprecation note.</summary>
    public const string AliasRemovalVersion = "2.11.0";

    /// <summary>All 28 canonical names, in the order of the findings §12 mapping.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        RepoStatus, IndexEnsureBaseline, IndexRefreshOverlay, IndexDiff, IndexListBaselines, IndexCleanup,
        IndexRemoveRepo, WorkspaceCreate, WorkspaceReset, WorkspaceList, WorkspaceDelete, SymbolsSearch,
        SymbolsGetCard, SymbolsGetContext, SymbolsGetDefinitionSpan, CodeGetSpan, CodeSearchText, RefsFind,
        GraphCallers, GraphCallees, GraphTraceFeature, TypesHierarchy, CodemapSummarize, CodemapExport,
        CodemapGuide, SurfacesListEndpoints, SurfacesListConfigKeys, SurfacesListDbTables,
    ];

    /// <summary>
    /// Deprecated dotted name → canonical name (28 entries). Immutable; kept until
    /// <see cref="AliasRemovalVersion"/> at the earliest (ADR-051). The dot always sits after the group
    /// word (<c>symbols.get_card</c> → <c>symbols_get_card</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> LegacyAliases { get; } =
        All.ToDictionary(n => ReplaceFirst(n, '_', '.'), n => n, StringComparer.Ordinal).AsReadOnly();

    private static string ReplaceFirst(string s, char from, char to)
    {
        var i = s.IndexOf(from);
        return s[..i] + to + s[(i + 1)..];
    }
}
