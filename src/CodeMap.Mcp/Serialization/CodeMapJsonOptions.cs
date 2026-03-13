namespace CodeMap.Mcp.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// System.Text.Json options for all CodeMap MCP serialization.
/// Uses snake_case property names, omits null fields, and serializes
/// identifier types (RepoId, CommitSha, etc.) as plain strings.
/// </summary>
public static class CodeMapJsonOptions
{
    /// <summary>The shared options instance for MCP tool responses.</summary>
    public static JsonSerializerOptions Default { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
        };

        opts.Converters.Add(new RepoIdJsonConverter());
        opts.Converters.Add(new CommitShaJsonConverter());
        opts.Converters.Add(new SymbolIdJsonConverter());
        opts.Converters.Add(new FilePathJsonConverter());
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        return opts;
    }
}
