namespace CodeMap.Mcp.Tests.Serialization;

using System.Text.Json;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;
using FluentAssertions;

/// <summary>
/// Tests that each identifier type serializes as a plain JSON string
/// (not as an object with a "value" property) and round-trips cleanly.
/// </summary>
public sealed class IdentifierConverterTests
{
    private static readonly JsonSerializerOptions _opts = CodeMapJsonOptions.Default;

    // ── RepoId ──────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_RepoId_SerializesAsPlainString()
    {
        var id = RepoId.From("my-repo");
        var json = JsonSerializer.Serialize(id, _opts);
        json.Should().Be("\"my-repo\"");
    }

    [Fact]
    public void Deserialize_RepoId_FromPlainString()
    {
        var id = JsonSerializer.Deserialize<RepoId>("\"my-repo\"", _opts);
        id.Value.Should().Be("my-repo");
    }

    [Fact]
    public void RoundTrip_RepoId_PreservesValue()
    {
        var original = RepoId.From("codemap-repo-abc123");
        var json = JsonSerializer.Serialize(original, _opts);
        var restored = JsonSerializer.Deserialize<RepoId>(json, _opts);
        restored.Should().Be(original);
    }

    // ── CommitSha ────────────────────────────────────────────────────────────

    private const string ValidSha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    [Fact]
    public void Serialize_CommitSha_SerializesAsPlainString()
    {
        var sha = CommitSha.From(ValidSha);
        var json = JsonSerializer.Serialize(sha, _opts);
        json.Should().Be($"\"{ValidSha}\"");
    }

    [Fact]
    public void Deserialize_CommitSha_FromPlainString()
    {
        var sha = JsonSerializer.Deserialize<CommitSha>($"\"{ValidSha}\"", _opts);
        sha.Value.Should().Be(ValidSha);
    }

    [Fact]
    public void RoundTrip_CommitSha_PreservesValue()
    {
        var original = CommitSha.From(ValidSha);
        var json = JsonSerializer.Serialize(original, _opts);
        var restored = JsonSerializer.Deserialize<CommitSha>(json, _opts);
        restored.Should().Be(original);
    }

    // ── SymbolId ─────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_SymbolId_SerializesAsPlainString()
    {
        var id = SymbolId.From("MyNamespace.MyClass.MyMethod");
        var json = JsonSerializer.Serialize(id, _opts);
        json.Should().Be("\"MyNamespace.MyClass.MyMethod\"");
    }

    [Fact]
    public void Deserialize_SymbolId_FromPlainString()
    {
        var id = JsonSerializer.Deserialize<SymbolId>("\"MyNamespace.MyClass\"", _opts);
        id.Value.Should().Be("MyNamespace.MyClass");
    }

    [Fact]
    public void RoundTrip_SymbolId_PreservesValue()
    {
        var original = SymbolId.From("CodeMap.Core.Models.SymbolCard");
        var json = JsonSerializer.Serialize(original, _opts);
        var restored = JsonSerializer.Deserialize<SymbolId>(json, _opts);
        restored.Should().Be(original);
    }

    // ── FilePath ─────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_FilePath_SerializesAsPlainString()
    {
        var path = FilePath.From("src/CodeMap.Core/Models/SymbolCard.cs");
        var json = JsonSerializer.Serialize(path, _opts);
        json.Should().Be("\"src/CodeMap.Core/Models/SymbolCard.cs\"");
    }

    [Fact]
    public void Deserialize_FilePath_FromPlainString()
    {
        var path = JsonSerializer.Deserialize<FilePath>("\"src/Foo.cs\"", _opts);
        path.Value.Should().Be("src/Foo.cs");
    }

    [Fact]
    public void RoundTrip_FilePath_PreservesValue()
    {
        var original = FilePath.From("tests/CodeMap.Mcp.Tests/Serialization/IdentifierConverterTests.cs");
        var json = JsonSerializer.Serialize(original, _opts);
        var restored = JsonSerializer.Deserialize<FilePath>(json, _opts);
        restored.Should().Be(original);
    }

    // ── Identifiers do NOT serialize as objects ───────────────────────────────

    [Fact]
    public void Serialize_RepoId_DoesNotProduceValueProperty()
    {
        var id = RepoId.From("test-repo");
        var json = JsonSerializer.Serialize(id, _opts);
        json.Should().NotContain("value");
    }

    [Fact]
    public void Serialize_CommitSha_DoesNotProduceValueProperty()
    {
        var sha = CommitSha.From(ValidSha);
        var json = JsonSerializer.Serialize(sha, _opts);
        json.Should().NotContain("value");
    }
}
