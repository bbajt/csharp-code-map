namespace CodeMap.Mcp;

/// <summary>
/// Registry of all MCP tools available on this server.
/// Handlers register tools here during DI setup.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools =
        new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) a tool definition.</summary>
    public void Register(ToolDefinition tool) => _tools[tool.Name] = tool;

    /// <summary>Returns all registered tools in registration order.</summary>
    public IReadOnlyList<ToolDefinition> GetAll() => [.. _tools.Values];

    /// <summary>Finds a tool by name, or returns null if not found.</summary>
    public ToolDefinition? Find(string name) =>
        _tools.TryGetValue(name, out var t) ? t : null;

    /// <summary>Number of registered tools.</summary>
    public int Count => _tools.Count;
}
