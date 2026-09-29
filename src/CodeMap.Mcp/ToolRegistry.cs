namespace CodeMap.Mcp;

/// <summary>
/// Registry of all MCP tools available on this server.
/// Handlers register tools here during DI setup. Deprecated names (ADR-051) are registered as
/// aliases: they resolve to their canonical tool but are not listed by <see cref="GetAll"/>.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _aliases =
        new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) a tool definition.</summary>
    public void Register(ToolDefinition tool) => _tools[tool.Name] = tool;

    /// <summary>Registers a deprecated alias for an already-registered canonical tool.</summary>
    /// <exception cref="InvalidOperationException">The canonical tool isn't registered.</exception>
    public void RegisterAlias(string alias, string canonicalName)
    {
        if (!_tools.ContainsKey(canonicalName))
            throw new InvalidOperationException(
                $"Cannot register alias '{alias}': tool '{canonicalName}' is not registered.");
        _aliases[alias] = canonicalName;
    }

    /// <summary>Returns all registered (canonical) tools in registration order. Aliases are not included.</summary>
    public IReadOnlyList<ToolDefinition> GetAll() => [.. _tools.Values];

    /// <summary>Registered aliases (alias → canonical name).</summary>
    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    /// <summary>Finds a tool by canonical name or alias, or returns null if not found.</summary>
    public ToolDefinition? Find(string name) => Find(name, out _);

    /// <summary>
    /// Finds a tool by canonical name or alias. <paramref name="usedAlias"/> is the alias when the
    /// tool was found through one, otherwise null.
    /// </summary>
    public ToolDefinition? Find(string name, out string? usedAlias)
    {
        usedAlias = null;
        if (_tools.TryGetValue(name, out var t))
            return t;
        if (_aliases.TryGetValue(name, out var canonical) && _tools.TryGetValue(canonical, out t))
        {
            usedAlias = name;
            return t;
        }
        return null;
    }

    /// <summary>Number of registered (canonical) tools. Aliases are not counted.</summary>
    public int Count => _tools.Count;
}
