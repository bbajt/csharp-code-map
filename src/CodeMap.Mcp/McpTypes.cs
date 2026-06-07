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
/// MCP tool annotations that hint at a tool's behaviour so clients can
/// categorise it without calling it (read-only, destructive, idempotent).
/// Maps to the <c>annotations</c> field in the 2025-03-26 MCP spec.
/// </summary>
/// <param name="ReadOnly">True when the tool only reads data and never modifies state.</param>
/// <param name="Destructive">True when the tool may cause irreversible side-effects (delete, purge).</param>
/// <param name="Idempotent">True when calling the tool multiple times with the same args has the same effect as calling it once.</param>
/// <param name="OpenWorld">True when the tool may contact external systems or have effects outside its local scope.</param>
public record ToolAnnotations(
    bool ReadOnly = false,
    bool Destructive = false,
    bool Idempotent = false,
    bool OpenWorld = true
);

/// <summary>A registered MCP tool with its schema and handler.</summary>
/// <param name="Name">MCP tool name, e.g. "symbols.search".</param>
/// <param name="Description">Human-readable description shown to agents.</param>
/// <param name="InputSchema">JSON Schema object describing accepted parameters.</param>
/// <param name="Handler">The async handler invoked when the tool is called.</param>
/// <param name="Annotations">Optional behavioural hints for MCP clients (2025-03-26 spec).</param>
public record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolHandler Handler,
    ToolAnnotations? Annotations = null
);
