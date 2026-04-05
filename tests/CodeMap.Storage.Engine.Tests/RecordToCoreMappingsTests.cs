namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using FluentAssertions;
using Xunit;

public sealed class RecordToCoreMappingsTests
{
    // ── ReverseSymbolKind ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, SymbolKind.Class)]
    [InlineData(2, SymbolKind.Interface)]
    [InlineData(3, SymbolKind.Record)]
    [InlineData(5, SymbolKind.Struct)]
    [InlineData(6, SymbolKind.Enum)]
    [InlineData(7, SymbolKind.Delegate)]
    [InlineData(8, SymbolKind.Method)]
    [InlineData(9, SymbolKind.Constructor)]
    [InlineData(10, SymbolKind.Field)]
    [InlineData(11, SymbolKind.Property)]
    [InlineData(12, SymbolKind.Event)]
    [InlineData(0, SymbolKind.Class)]   // Unknown → Class fallback
    [InlineData(99, SymbolKind.Class)]  // Unknown → Class fallback
    public void ReverseSymbolKind_AllValues(short v2Kind, SymbolKind expected)
        => RecordToCoreMappings.ReverseSymbolKind(v2Kind).Should().Be(expected);

    // ── ReverseEdgeKind ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, RefKind.Call)]
    [InlineData(2, RefKind.Read)]
    [InlineData(3, RefKind.Write)]
    [InlineData(5, RefKind.Implementation)]
    [InlineData(6, RefKind.Override)]
    public void ReverseEdgeKind_AllValues(short edgeKind, RefKind expected)
        => RecordToCoreMappings.ReverseEdgeKind(edgeKind).Should().Be(expected);

    // ── ReverseAccessibility ─────────────────────────────────────────────────

    [Theory]
    [InlineData(7, "public")]
    [InlineData(4, "internal")]
    [InlineData(3, "protected")]
    [InlineData(1, "private")]
    [InlineData(2, "protected internal")]
    [InlineData(6, "private protected")]
    [InlineData(0, "public")]  // fallback
    public void ReverseAccessibility_AllValues(short code, string expected)
        => RecordToCoreMappings.ReverseAccessibility(code).Should().Be(expected);

    // ── ReverseConfidence ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, Confidence.High)]
    [InlineData(1, Confidence.Medium)]
    [InlineData(2, Confidence.Low)]
    [InlineData(99, Confidence.High)]  // fallback
    public void ReverseConfidence_AllValues(int code, Confidence expected)
        => RecordToCoreMappings.ReverseConfidence(code).Should().Be(expected);

    // ── Round-trip: Forward + Reverse ────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Interface)]
    [InlineData(SymbolKind.Method)]
    [InlineData(SymbolKind.Property)]
    [InlineData(SymbolKind.Event)]
    public void SymbolKind_RoundTrip(SymbolKind original)
    {
        var v2 = RecordMappers.MapSymbolKind(original);
        var reversed = RecordToCoreMappings.ReverseSymbolKind(v2);
        reversed.Should().Be(original);
    }

    [Theory]
    [InlineData(RefKind.Call)]
    [InlineData(RefKind.Read)]
    [InlineData(RefKind.Write)]
    [InlineData(RefKind.Override)]
    [InlineData(RefKind.Implementation)]
    public void EdgeKind_RoundTrip(RefKind original)
    {
        var v2 = RecordMappers.MapEdgeKind(original);
        var reversed = RecordToCoreMappings.ReverseEdgeKind(v2);
        reversed.Should().Be(original);
    }

    [Theory]
    [InlineData(Confidence.High)]
    [InlineData(Confidence.Medium)]
    [InlineData(Confidence.Low)]
    public void Confidence_RoundTrip(Confidence original)
    {
        var v2 = RecordMappers.MapConfidence(original);
        var reversed = RecordToCoreMappings.ReverseConfidence(v2);
        reversed.Should().Be(original);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("protected")]
    [InlineData("private")]
    public void Accessibility_RoundTrip(string vis)
    {
        var v2 = RecordMappers.MapAccessibility(vis);
        var reversed = RecordToCoreMappings.ReverseAccessibility(v2);
        reversed.Should().Be(vis);
    }
}
