namespace CodeMap.Core.Models;

/// <summary>
/// Top-level response payload for the <c>codemap.guide</c> MCP tool.
/// Contains everything an agent needs to start a CodeMap session correctly.
/// </summary>
public sealed record GuideResponse(
    /// <summary>CodeMap server version (from assembly informational version attribute).</summary>
    string Version,
    /// <summary>Session setup commands to run at the start of every session.</summary>
    GuideSessionStart SessionStart,
    /// <summary>Decision table mapping agent tasks to the correct CodeMap tool.</summary>
    IReadOnlyList<GuideDecisionEntry> DecisionTable,
    /// <summary>Usage rules that must always be followed when calling CodeMap tools.</summary>
    IReadOnlyList<string> Rules,
    /// <summary>Command to run after every file edit to keep CodeMap in sync.</summary>
    string AfterEditCommand,
    /// <summary>Total number of MCP tools registered in this server instance.</summary>
    int ToolCount,
    /// <summary>
    /// Full tool list with descriptions. Null unless <c>verbose: true</c> was requested.
    /// </summary>
    IReadOnlyList<GuideToolEntry>? Tools = null
);

/// <summary>Session setup commands an agent must run before any code work.</summary>
public sealed record GuideSessionStart(
    /// <summary>Human-readable description of when to run these commands.</summary>
    string Description,
    /// <summary>
    /// Ordered list of commands to run. First: index.ensure_baseline. Second: workspace.create.
    /// </summary>
    IReadOnlyList<string> Commands
);

/// <summary>
/// One row of the decision table: which CodeMap tool to use for a given task,
/// and which tool to avoid using instead.
/// </summary>
public sealed record GuideDecisionEntry(
    /// <summary>The task the agent is trying to accomplish.</summary>
    string Task,
    /// <summary>The CodeMap tool to use for this task.</summary>
    string UseTool,
    /// <summary>What NOT to use — the tempting but wrong approach (e.g. grep, Read file).</summary>
    string NotThis
);

/// <summary>One entry in the verbose tool list.</summary>
public sealed record GuideToolEntry(
    /// <summary>MCP tool name (e.g. <c>symbols.search</c>).</summary>
    string Name,
    /// <summary>One-line description of what the tool does.</summary>
    string Description
);
