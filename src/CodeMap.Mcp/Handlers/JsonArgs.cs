namespace CodeMap.Mcp.Handlers;

using System.Text.Json.Nodes;

/// <summary>
/// Helpers for reading MCP tool arguments from <see cref="JsonObject"/>.
/// MCP clients (including Claude) occasionally send numeric parameters as JSON
/// strings (e.g. <c>"max_tokens": "8000"</c> instead of <c>"max_tokens": 8000</c>).
/// These helpers handle both forms transparently.
/// </summary>
internal static class JsonArgs
{
    /// <summary>Returns the integer value of a parameter, or null if absent or unparseable.</summary>
    public static int? GetInt(this JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var si)) return si;
        }
        return null;
    }

    /// <summary>Returns the integer value of a parameter, or <paramref name="defaultValue"/> if absent.</summary>
    public static int GetInt(this JsonObject? args, string key, int defaultValue)
        => args.GetInt(key) ?? defaultValue;
}
