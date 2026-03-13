namespace CodeMap.Mcp.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.Core.Types;

/// <summary>Serializes RepoId as a plain string.</summary>
public sealed class RepoIdJsonConverter : JsonConverter<RepoId>
{
    public override RepoId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => RepoId.From(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, RepoId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Serializes CommitSha as a plain string.</summary>
public sealed class CommitShaJsonConverter : JsonConverter<CommitSha>
{
    public override CommitSha Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => CommitSha.From(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, CommitSha value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Serializes SymbolId as a plain string.</summary>
public sealed class SymbolIdJsonConverter : JsonConverter<SymbolId>
{
    public override SymbolId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => SymbolId.From(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SymbolId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Serializes FilePath as a plain string.</summary>
public sealed class FilePathJsonConverter : JsonConverter<FilePath>
{
    public override FilePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FilePath.From(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
