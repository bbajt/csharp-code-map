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

/// <summary>A registered MCP tool with its schema and handler.</summary>
/// <param name="Name">MCP tool name, e.g. "symbols.search".</param>
/// <param name="Description">Human-readable description shown to agents.</param>
/// <param name="InputSchema">JSON Schema object describing accepted parameters.</param>
/// <param name="Handler">The async handler invoked when the tool is called.</param>
public record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolHandler Handler
);
