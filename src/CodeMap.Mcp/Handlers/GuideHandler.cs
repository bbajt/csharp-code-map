namespace CodeMap.Mcp.Handlers;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Models;
using CodeMap.Mcp.Serialization;

/// <summary>
/// Handles the <c>codemap.guide</c> MCP tool (#28).
/// </summary>
/// <remarks>
/// <b>codemap.guide</b> requires no parameters. Returns a structured quick-start guide
/// containing session setup commands, decision table, and usage rules.
///
/// Designed to be self-discoverable: agents that scan the tool manifest before acting
/// will see <c>codemap.guide</c> and call it first — getting correct guidance without
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
        new("Find a class, method, interface by name",         "symbols.search",             "grep, cat, find"),
        new("Understand a method's code + what it calls",      "symbols.get_context",        "Read file"),
        new("Read a method's source code",                     "symbols.get_card",           "Read file"),
        new("Find who calls a method",                         "graph.callers",              "grep"),
        new("Find what a method calls",                        "graph.callees",              "grep"),
        new("Trace a feature end-to-end",                      "graph.trace_feature",        "manual graph traversal"),
        new("Find all usages of a symbol",                     "refs.find",                  "grep"),
        new("Check what a class implements or inherits",       "types.hierarchy",            "grep"),
        new("Search for text or string literals in source",    "code.search_text",           "grep"),
        new("Understand overall architecture",                 "codemap.summarize",          "reading many files"),
        new("See all HTTP endpoints",                          "surfaces.list_endpoints",    "grep for [Route]"),
        new("See all config key usage",                        "surfaces.list_config_keys",  "grep for IConfiguration"),
        new("See all database tables",                         "surfaces.list_db_tables",    "grep for DbSet"),
        // BUG-5 (M20-02): surfaces.list_di_registrations is advertised in
        // codemap.summarize and KNOWN-LIMITATIONS but not yet a registered
        // MCP tool — DI facts are still extracted (FactKind.DiRegistration)
        // and surface in summarize. Listing the missing tool here would
        // produce NOT_FOUND on first call. Implementation deferred.
    ];

    private static readonly IReadOnlyList<string> Rules =
    [
        "Always include workspace_id: 'session' in every query. Without it, queries only see committed code — your in-progress edits are invisible.",
        "After editing any C# file: run index.refresh_overlay { repo_path: \".\", workspace_id: \"session\" } (~63ms). Without it, CodeMap does not see your changes.",
        "Never type FQNs manually. Roslyn doc-comment IDs are exact — one wrong character returns NOT_FOUND. Always call symbols.search first and copy the symbol_id from the result.",
        "Write XML doc comments (/// <summary>) on all public and internal classes, methods, and interfaces. CodeMap indexes them — good docs dramatically improve symbols.get_card and symbols.get_context quality.",
    ];

    private static readonly IReadOnlyList<GuideToolEntry> AllTools =
    [
        new("repo.status",                 "Check if a repo is indexed and get index health."),
        new("index.ensure_baseline",       "Build or verify a semantic index for a .NET solution."),
        new("index.refresh_overlay",       "Refresh workspace overlay after editing files (~63ms)."),
        new("index.diff",                  "Semantic diff between two commits or a commit and workspace."),
        new("index.list_baselines",        "List all cached baseline indexes for a repo."),
        new("index.cleanup",               "Remove stale or orphaned baseline index files."),
        new("index.remove_repo",           "Remove all indexes for a repository."),
        new("workspace.create",            "Create an isolated session for overlay indexing."),
        new("workspace.reset",             "Reset a workspace overlay to the baseline commit."),
        new("workspace.list",              "List all active workspaces and their staleness."),
        new("workspace.delete",            "Delete a workspace and free its overlay files."),
        new("symbols.search",              "Find symbols (class, method, interface) by name."),
        new("symbols.get_card",            "Get a symbol's metadata, facts, and source code."),
        new("symbols.get_context",         "Card + source + callee cards in one call."),
        new("symbols.get_definition_span", "Get exact source location for a symbol."),
        new("code.get_span",               "Read a range of source lines from a file."),
        new("code.search_text",            "Search for text patterns across all source files."),
        new("refs.find",                   "Find all references to a symbol."),
        new("graph.callers",               "Find methods that call a given method."),
        new("graph.callees",               "Find methods called by a given method."),
        new("graph.trace_feature",         "Full annotated feature flow from an entry point."),
        new("types.hierarchy",             "Get base types, interfaces, and derived types."),
        new("surfaces.list_endpoints",     "List all HTTP endpoints with routes and handlers."),
        new("surfaces.list_config_keys",   "List all configuration key usages."),
        new("surfaces.list_db_tables",     "List all database tables and their sources."),
        new("codemap.summarize",           "Full codebase overview: endpoints, DI, config, DB, middleware."),
        new("codemap.export",              "Portable context dump (markdown/JSON) for any LLM."),
        new("codemap.guide",               "This tool. Quick-start guide: session setup, decision table, rules."),
    ];

    private static readonly GuideSessionStart SessionStart = new(
        "Run these two commands at the start of every session, before any code work. Fast (<2s total).",
        [
            "index.ensure_baseline { repo_path: \"<absolute_repo_path>\" }",
            "workspace.create { repo_path: \"<absolute_repo_path>\", workspace_id: \"session\" }",
        ]
    );

    private const string AfterEditCommand =
        "index.refresh_overlay { repo_path: \".\", workspace_id: \"session\" }";

    private const string KnownLimitationsHint =
        "If a search returns nothing where you expect a hit, scan KNOWN-LIMITATIONS.md " +
        "first. Common causes: multi-target conditional symbols (only highest TFM is " +
        "indexed), legacy MVC MapControllerRoute (not extracted), F# fact extractors " +
        "not yet wired, fresh clone needs a build for Razor SG output.";

    private const string KnownLimitationsDoc = "docs/KNOWN-LIMITATIONS.md";

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

    /// <summary>Registers <c>codemap.guide</c> into the ToolRegistry as tool #28.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "codemap.guide",
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
            HandleGetGuideAsync));
    }

    internal Task<ToolCallResult> HandleGetGuideAsync(JsonObject? args, CancellationToken ct)
    {
        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;

        var guide = new GuideResponse(
            Version:                _version,
            SessionStart:           SessionStart,
            DecisionTable:          DecisionTable,
            Rules:                  Rules,
            AfterEditCommand:       AfterEditCommand,
            ToolCount:              AllTools.Count,
            KnownLimitationsHint:   KnownLimitationsHint,
            KnownLimitationsDoc:    KnownLimitationsDoc,
            Tools:                  verbose ? AllTools : null
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
