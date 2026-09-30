namespace CodeMap.Mcp.Handlers;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Models;
using CodeMap.Mcp.Serialization;

/// <summary>
/// Handles the <c>codemap_guide</c> MCP tool (#28).
/// </summary>
/// <remarks>
/// <b>codemap_guide</b> requires no parameters. Returns a structured quick-start guide
/// containing session setup commands, decision table, and usage rules.
///
/// Designed to be self-discoverable: agents that scan the tool manifest before acting
/// will see <c>codemap_guide</c> and call it first — getting correct guidance without
/// reading any file.
///
/// Optional: <c>verbose: true</c> appends a full list of all 28 tools with descriptions.
///
/// No IQueryEngine or IGitService dependency — guide is always available even if the
/// query engine or git service is misconfigured.
/// </remarks>
public sealed class GuideHandler
{
    private static readonly IReadOnlyList<GuideDecisionEntry> DecisionTable =
    [
        new("Find a class, method, interface by name",      ToolNames.SymbolsSearch,           "grep, cat, find"),
        new("Understand a method's code + what it calls",   ToolNames.SymbolsGetContext,       "Read file"),
        new("Read a method's source code",                  ToolNames.SymbolsGetCard,          "Read file"),
        new("Find who calls a method",                      $"{ToolNames.GraphCallers} (set follow_interface=true when the target implements an interface)", "grep"),
        new("Find what a method calls",                     ToolNames.GraphCallees,            "grep"),
        new("Trace a feature end-to-end",                   ToolNames.GraphTraceFeature,       "manual graph traversal"),
        new("Find all usages of a symbol",                  ToolNames.RefsFind,                "grep"),
        new("Check what a class implements or inherits",    ToolNames.TypesHierarchy,          "grep"),
        new("Search for text or string literals in source", ToolNames.CodeSearchText,          "grep"),
        new("Understand overall architecture",              ToolNames.CodemapSummarize,        "reading many files"),
        new("See all HTTP endpoints",                       ToolNames.SurfacesListEndpoints,   "grep for [Route]"),
        new("See all config key usage",                     ToolNames.SurfacesListConfigKeys,  "grep for IConfiguration"),
        new("See all database tables",                      ToolNames.SurfacesListDbTables,    "grep for DbSet"),
        // BUG-5 (M20-02): surfaces.list_di_registrations is advertised in
        // codemap.summarize and KNOWN-LIMITATIONS but not yet a registered
        // MCP tool — DI facts are still extracted (FactKind.DiRegistration)
        // and surface in summarize. Listing the missing tool here would
        // produce NOT_FOUND on first call. Implementation deferred.
    ];

    private static readonly IReadOnlyList<string> Rules =
    [
        "Always include workspace_id: 'session' in every query. Without it, queries only see committed code — your in-progress edits are invisible.",
        "After editing any C# file: run index_refresh_overlay { repo_path: \".\", workspace_id: \"session\" } (~63ms). Without it, CodeMap does not see your changes.",
        "Never type FQNs manually. Roslyn doc-comment IDs are exact — one wrong character returns NOT_FOUND. Always call symbols_search first and copy the symbol_id from the result.",
        "Write XML doc comments (/// <summary>) on all public and internal classes, methods, and interfaces. CodeMap indexes them — good docs dramatically improve symbols_get_card and symbols_get_context quality.",
    ];

    private static readonly IReadOnlyList<GuideToolEntry> AllTools =
    [
        new(ToolNames.RepoStatus,                     "Check if a repo is indexed and get index health."),
        new(ToolNames.IndexEnsureBaseline,            "Build or verify a semantic index for a .NET solution."),
        new(ToolNames.IndexRefreshOverlay,            "Refresh workspace overlay after editing files (~63ms)."),
        new(ToolNames.IndexDiff,                      "Semantic diff between two commits or a commit and workspace."),
        new(ToolNames.IndexListBaselines,             "List all cached baseline indexes for a repo."),
        new(ToolNames.IndexCleanup,                   "Remove stale or orphaned baseline index files."),
        new(ToolNames.IndexRemoveRepo,                "Remove all indexes for a repository."),
        new(ToolNames.WorkspaceCreate,                "Create an isolated session for overlay indexing."),
        new(ToolNames.WorkspaceReset,                 "Reset a workspace overlay to the baseline commit."),
        new(ToolNames.WorkspaceList,                  "List all active workspaces and their staleness."),
        new(ToolNames.WorkspaceDelete,                "Delete a workspace and free its overlay files."),
        new(ToolNames.SymbolsSearch,                  "Find symbols (class, method, interface) by name."),
        new(ToolNames.SymbolsGetCard,                 "Get a symbol's metadata, facts, and source code."),
        new(ToolNames.SymbolsGetContext,              "Card + source + callee cards in one call."),
        new(ToolNames.SymbolsGetDefinitionSpan,       "Get exact source location for a symbol."),
        new(ToolNames.CodeGetSpan,                    "Read a range of source lines from a file."),
        new(ToolNames.CodeSearchText,                 "Search for text patterns across all source files."),
        new(ToolNames.RefsFind,                       "Find all references to a symbol."),
        new(ToolNames.GraphCallers,                   "Find methods that call a given method. Emits interface_implementation_hint when the target implements an interface; pass follow_interface=true to union those callers."),
        new(ToolNames.GraphCallees,                   "Find methods called by a given method."),
        new(ToolNames.GraphTraceFeature,              "Full annotated feature flow from an entry point."),
        new(ToolNames.TypesHierarchy,                 "Get base types, interfaces, and derived types."),
        new(ToolNames.SurfacesListEndpoints,          "List all HTTP endpoints with routes and handlers."),
        new(ToolNames.SurfacesListConfigKeys,         "List all configuration key usages."),
        new(ToolNames.SurfacesListDbTables,           "List all database tables and their sources."),
        new(ToolNames.CodemapSummarize,               "Full codebase overview: endpoints, DI, config, DB, middleware."),
        new(ToolNames.CodemapExport,                  "Portable context dump (markdown/JSON) for any LLM."),
        new(ToolNames.CodemapGuide,                   "This tool. Quick-start guide: session setup, decision table, rules."),
    ];

    private static readonly GuideSessionStart SessionStart = new(
        "Run these two commands at the start of every session, before any code work. Fast (<2s total).",
        [
            "index_ensure_baseline { repo_path: \"<absolute_repo_path>\" }",
            "workspace_create { repo_path: \"<absolute_repo_path>\", workspace_id: \"session\" }",
        ]
    );

    private const string AfterEditCommand =
        "index_refresh_overlay { repo_path: \".\", workspace_id: \"session\" }";

    private const string KnownLimitationsHint =
        "If a search returns nothing where you expect a hit, scan KNOWN-LIMITATIONS.md " +
        "first. Common causes: multi-target conditional symbols (only highest TFM is " +
        "indexed), legacy MVC MapControllerRoute (not extracted), F# fact extractors " +
        "not yet wired, fresh clone needs a build for Razor SG output.";

    private const string KnownLimitationsDoc = "docs/KNOWN-LIMITATIONS.md";

    private static readonly GuideDeprecatedAliases DeprecatedAliases = new(
        "Before v2.9.0 tool names used a dot instead of the first underscore. Those names still work as " +
        "deprecated aliases and add a note to the response. Call the names listed here.",
        ToolNames.AliasRemovalVersion);

    private readonly string _version;

    /// <summary>
    /// Initializes the GuideHandler, reading the server version from the entry assembly.
    /// </summary>
    public GuideHandler()
    {
        _version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
    }

    /// <summary>Registers <c>codemap_guide</c> into the ToolRegistry as tool #28.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            ToolNames.CodemapGuide,
            "Returns the CodeMap quick-start guide: session setup commands, decision table " +
            "(which tool to use for each task), and usage rules. No parameters required. " +
            "Call this at the start of a new session or whenever you are unsure which CodeMap tool to use.",
            BuildSchema(
                required: [],
                properties: new JsonObject
                {
                    ["verbose"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include full list of all 28 tools with descriptions (default: false).",
                    },
                }),
            HandleGetGuideAsync,
            HandlerHelpers.AnnotReadOnly));
    }

    internal Task<ToolCallResult> HandleGetGuideAsync(JsonObject? args, CancellationToken ct)
    {
        var verbose = args.GetBool("verbose", false);

        var guide = new GuideResponse(
            Version:                _version,
            SessionStart:           SessionStart,
            DecisionTable:          DecisionTable,
            Rules:                  Rules,
            AfterEditCommand:       AfterEditCommand,
            ToolCount:              AllTools.Count,
            KnownLimitationsHint:   KnownLimitationsHint,
            KnownLimitationsDoc:    KnownLimitationsDoc,
            Tools:                  verbose ? AllTools : null,
            DeprecatedAliases:      DeprecatedAliases
        );

        return Task.FromResult(Ok(guide));
    }

    private static JsonObject BuildSchema(string[] required, JsonObject properties) =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["properties"] = properties,
        };

    private static ToolCallResult Ok<T>(T value) =>
        new(JsonSerializer.Serialize(value, CodeMapJsonOptions.Default));
}
