namespace CodeMap.Mcp;

using System.Text.Json.Nodes;

/// <summary>Handler delegate for a registered MCP tool.</summary>
/// <param name="arguments">The parsed JSON arguments object from the tool call.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>A <see cref="ToolCallResult"/> with the serialized response content.</returns>
public delegate Task<ToolCallResult> ToolHandler(JsonObject? arguments, CancellationToken ct);

/// <summary>Result returned by a tool handler.</summary>
/// <param name="Content">JSON-serialized response payload (tool-specific type).</param>
/// <param name="IsError">True if the tool encountered an error (returns isError=true to MCP).</param>
public record ToolCallResult(string Content, bool IsError = false);

/// <summary>
/// Behavioural hints for an MCP tool per the 2025-03-26 spec (the <c>annotations</c>
/// field on <c>tools/list</c> entries). Clients use these to categorize tools
/// without invoking them — Claude Desktop auto-approves <see cref="ReadOnly"/>
/// calls, gates <see cref="Destructive"/> calls behind explicit user approval,
/// and uses <see cref="Idempotent"/> to decide whether to retry on transient
/// failures.
/// </summary>
/// <remarks>
/// <para><b>Prefer the <c>HandlerHelpers.Annot*</c> presets to constructing this
/// directly.</b> CodeMap is a fully local, closed-world tool surface — every
/// registered tool operates on a memory-mapped on-disk index, never the network
/// or external state — so all presets set <see cref="OpenWorld"/> to false.
/// Constructing this record directly with positional args (or omitting
/// <see cref="OpenWorld"/>) also produces a closed-world annotation, which is
/// the safe project-wide default. The MCP spec's broader default is open-world
/// (conservative for tools that may touch the network); we deliberately diverge
/// because CodeMap's local-only invariant lets clients auto-approve more aggressively.</para>
/// </remarks>
/// <param name="ReadOnly">True when the tool only reads data and never modifies state.</param>
/// <param name="Destructive">True when the tool may cause irreversible side-effects (delete, purge, reset).</param>
/// <param name="Idempotent">True when calling the tool N times with the same args is observationally equivalent to calling it once.</param>
/// <param name="OpenWorld">True when the tool may contact external systems or have effects outside its local scope. Defaults to false for CodeMap (closed-world).</param>
public record ToolAnnotations(
    bool ReadOnly = false,
    bool Destructive = false,
    bool Idempotent = false,
    bool OpenWorld = false
);

/// <summary>A registered MCP tool with its schema and handler.</summary>
/// <param name="Name">MCP tool name, e.g. "symbols_search".</param>
/// <param name="Description">Human-readable description shown to agents.</param>
/// <param name="InputSchema">JSON Schema object describing accepted parameters.</param>
/// <param name="Handler">The async handler invoked when the tool is called.</param>
/// <param name="Annotations">Optional behavioural hints (2025-03-26 MCP spec). When null, no <c>annotations</c> key is emitted in <c>tools/list</c> — fully backward-compatible.</param>
public record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolHandler Handler,
    ToolAnnotations? Annotations = null
);
