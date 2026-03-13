namespace CodeMap.Mcp.Tests.Serialization;

using System.Text.Json;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Serialization;
using FluentAssertions;

/// <summary>
/// Tests that CodeMapJsonOptions produces correct snake_case JSON,
/// omits null fields, and round-trips complex types faithfully.
/// </summary>
public sealed class JsonSerializationTests
{
    private static readonly JsonSerializerOptions _opts = CodeMapJsonOptions.Default;

    private const string ValidSha = "0000000000000000000000000000000000000001";

    // ── Snake-case ────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ResponseEnvelope_ProducesSnakeCaseJson()
    {
        var envelope = BuildMinimalEnvelope("hello");
        var json = JsonSerializer.Serialize(envelope, _opts);

        json.Should().Contain("\"answer\"");
        json.Should().Contain("\"next_actions\"");
        json.Should().Contain("\"evidence\"");
        json.Should().Contain("\"confidence\"");
        json.Should().Contain("\"meta\"");
    }

    [Fact]
    public void Serialize_SymbolCard_ProducesSnakeCaseJson()
    {
        var card = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From("MyNs.MyClass"),
            fullyQualifiedName: "MyNs.MyClass",
            kind: SymbolKind.Class,
            signature: "public class MyClass",
            @namespace: "MyNs",
            filePath: FilePath.From("src/MyClass.cs"),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High);

        var json = JsonSerializer.Serialize(card, _opts);

        json.Should().Contain("\"symbol_id\"");
        json.Should().Contain("\"fully_qualified_name\"");
        json.Should().Contain("\"span_start\"");
        json.Should().Contain("\"span_end\"");
        json.Should().Contain("\"calls_top\"");
        // containing_type is null, so it must be omitted
        json.Should().NotContain("\"containing_type\"");
    }

    [Fact]
    public void Serialize_TimingBreakdown_ProducesSnakeCaseJson()
    {
        var timing = new TimingBreakdown(TotalMs: 42.5, DbQueryMs: 10.0);
        var json = JsonSerializer.Serialize(timing, _opts);

        json.Should().Contain("\"total_ms\"");
        json.Should().Contain("\"db_query_ms\"");
        json.Should().Contain("\"cache_lookup_ms\"");
    }

    // ── Null omission ─────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_NullFields_OmittedFromJson()
    {
        var card = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From("MyNs.MyClass"),
            fullyQualifiedName: "MyNs.MyClass",
            kind: SymbolKind.Class,
            signature: "public class MyClass",
            @namespace: "MyNs",
            filePath: FilePath.From("src/MyClass.cs"),
            spanStart: 1,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High,
            documentation: null,
            containingType: null);

        var json = JsonSerializer.Serialize(card, _opts);

        // Nullable string fields with null value must be omitted
        json.Should().NotContain("\"documentation\"");
        json.Should().NotContain("\"containing_type\"");
    }

    [Fact]
    public void Serialize_NextAction_NullParameters_Omitted()
    {
        var action = new NextAction("symbols.search", "Look for more symbols");
        var json = JsonSerializer.Serialize(action, _opts);

        json.Should().Contain("\"tool\"");
        json.Should().Contain("\"rationale\"");
        json.Should().NotContain("\"parameters\"");
    }

    // ── Empty list ────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_EmptyList_SerializesAsEmptyArray()
    {
        var card = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From("X.Y"),
            fullyQualifiedName: "X.Y",
            kind: SymbolKind.Method,
            signature: "void Y()",
            @namespace: "X",
            filePath: FilePath.From("src/Y.cs"),
            spanStart: 1,
            spanEnd: 5,
            visibility: "public",
            confidence: Confidence.High);

        var json = JsonSerializer.Serialize(card, _opts);

        json.Should().Contain("\"calls_top\":[]");
        json.Should().Contain("\"facts\":[]");
        json.Should().Contain("\"side_effects\":[]");
        json.Should().Contain("\"thrown_exceptions\":[]");
        json.Should().Contain("\"evidence\":[]");
    }

    // ── Enum serialization ────────────────────────────────────────────────────

    [Fact]
    public void Serialize_SymbolKind_UsesSnakeCaseLower()
    {
        var json = JsonSerializer.Serialize(SymbolKind.Record, _opts);
        json.Should().Be("\"record\"");
    }

    [Fact]
    public void Serialize_Confidence_UsesSnakeCaseLower()
    {
        var json = JsonSerializer.Serialize(Confidence.High, _opts);
        json.Should().Be("\"high\"");
    }

    // ── Round-trips ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_SymbolCard_PreservesAllFields()
    {
        var original = SymbolCard.CreateMinimal(
            symbolId: SymbolId.From("CodeMap.Core.Models.SymbolCard"),
            fullyQualifiedName: "CodeMap.Core.Models.SymbolCard",
            kind: SymbolKind.Class,
            signature: "public record SymbolCard(...)",
            @namespace: "CodeMap.Core.Models",
            filePath: FilePath.From("src/CodeMap.Core/Models/SymbolCard.cs"),
            spanStart: 7,
            spanEnd: 54,
            visibility: "public",
            confidence: Confidence.High,
            documentation: "Full structured symbol card.",
            containingType: null);

        var json = JsonSerializer.Serialize(original, _opts);
        var restored = JsonSerializer.Deserialize<SymbolCard>(json, _opts)!;

        restored.SymbolId.Should().Be(original.SymbolId);
        restored.FullyQualifiedName.Should().Be(original.FullyQualifiedName);
        restored.Kind.Should().Be(original.Kind);
        restored.Signature.Should().Be(original.Signature);
        restored.FilePath.Should().Be(original.FilePath);
        restored.SpanStart.Should().Be(original.SpanStart);
        restored.SpanEnd.Should().Be(original.SpanEnd);
        restored.Confidence.Should().Be(original.Confidence);
        restored.Documentation.Should().Be(original.Documentation);
    }

    [Fact]
    public void RoundTrip_CodeMapError_PreservesAllFields()
    {
        var original = CodeMapError.BudgetExceeded("max_lines", 200, 120);
        var json = JsonSerializer.Serialize(original, _opts);
        var restored = JsonSerializer.Deserialize<CodeMapError>(json, _opts)!;

        restored.Code.Should().Be(original.Code);
        restored.Message.Should().Be(original.Message);
        restored.Retryable.Should().Be(original.Retryable);
    }

    [Fact]
    public void RoundTrip_ResponseEnvelope_PreservesAllFields()
    {
        var envelope = BuildMinimalEnvelope("Returned 3 symbols.");
        var json = JsonSerializer.Serialize(envelope, _opts);
        var restored = JsonSerializer.Deserialize<ResponseEnvelope<string>>(json, _opts)!;

        restored.Answer.Should().Be(envelope.Answer);
        restored.Data.Should().Be(envelope.Data);
        restored.Confidence.Should().Be(envelope.Confidence);
        restored.Evidence.Should().BeEmpty();
        restored.NextActions.Should().BeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ResponseEnvelope<string> BuildMinimalEnvelope(string answer) =>
        new(
            Answer: answer,
            Data: "data-payload",
            Evidence: [],
            NextActions: [],
            Confidence: Confidence.High,
            Meta: new ResponseMeta(
                Timing: new TimingBreakdown(TotalMs: 1.0),
                BaselineCommitSha: CommitSha.From(ValidSha),
                LimitsApplied: new Dictionary<string, LimitApplied>(),
                TokensSaved: 0,
                CostAvoided: 0m));
}
