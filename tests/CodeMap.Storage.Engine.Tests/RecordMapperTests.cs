namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using FluentAssertions;
using Xunit;

public sealed class RecordMapperTests
{
    // ── MapSymbolKind ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Class, 1)]
    [InlineData(SymbolKind.Interface, 2)]
    [InlineData(SymbolKind.Record, 3)]
    [InlineData(SymbolKind.Struct, 5)]
    [InlineData(SymbolKind.Enum, 6)]
    [InlineData(SymbolKind.Delegate, 7)]
    [InlineData(SymbolKind.Method, 8)]
    [InlineData(SymbolKind.Constructor, 9)]
    [InlineData(SymbolKind.Field, 10)]
    [InlineData(SymbolKind.Property, 11)]
    [InlineData(SymbolKind.Event, 12)]
    [InlineData(SymbolKind.Constant, 10)]  // stored as Field
    [InlineData(SymbolKind.Indexer, 11)]   // stored as Property
    [InlineData(SymbolKind.Operator, 8)]   // stored as Method
    public void MapSymbolKind_AllValues(SymbolKind kind, short expected)
        => RecordMappers.MapSymbolKind(kind).Should().Be(expected);

    // ── MapAccessibility ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("public", 7)]
    [InlineData("internal", 4)]
    [InlineData("protected", 3)]
    [InlineData("private", 1)]
    [InlineData("protected internal", 2)]
    [InlineData("private protected", 6)]
    [InlineData(null, 0)]
    [InlineData("unknown", 0)]
    public void MapAccessibility_AllValues(string? vis, short expected)
        => RecordMappers.MapAccessibility(vis).Should().Be(expected);

    // ── MapEdgeKind ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RefKind.Call, 1)]
    [InlineData(RefKind.Read, 2)]
    [InlineData(RefKind.Write, 3)]
    [InlineData(RefKind.Instantiate, 1)]     // treated as Call
    [InlineData(RefKind.Override, 6)]
    [InlineData(RefKind.Implementation, 5)]
    public void MapEdgeKind_AllValues(RefKind kind, short expected)
        => RecordMappers.MapEdgeKind(kind).Should().Be(expected);

    // ── MapResolutionState ───────────────────────────────────────────────────

    [Theory]
    [InlineData(ResolutionState.Resolved, 0)]
    [InlineData(ResolutionState.Unresolved, 1)]
    public void MapResolutionState_AllValues(ResolutionState state, short expected)
        => RecordMappers.MapResolutionState(state).Should().Be(expected);

    // ── MapConfidence ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Confidence.High, 0)]
    [InlineData(Confidence.Medium, 1)]
    [InlineData(Confidence.Low, 2)]
    public void MapConfidence_AllValues(Confidence conf, int expected)
        => RecordMappers.MapConfidence(conf).Should().Be(expected);

    // ── ComputeDegradedStableId ──────────────────────────────────────────────

    [Fact]
    public void ComputeDegradedStableId_StartsWithSym()
    {
        var id = RecordMappers.ComputeDegradedStableId(SymbolKind.Class, "T:Foo", "MyApp");
        id.Should().StartWith("sym_");
        id.Length.Should().Be(20); // "sym_" + 16 hex chars
    }

    [Fact]
    public void ComputeDegradedStableId_Deterministic()
    {
        var id1 = RecordMappers.ComputeDegradedStableId(SymbolKind.Method, "M:Foo.Bar", "Proj");
        var id2 = RecordMappers.ComputeDegradedStableId(SymbolKind.Method, "M:Foo.Bar", "Proj");
        id1.Should().Be(id2);
    }

    [Fact]
    public void ComputeDegradedStableId_DiffersForDifferentInputs()
    {
        var id1 = RecordMappers.ComputeDegradedStableId(SymbolKind.Class, "T:Foo", "A");
        var id2 = RecordMappers.ComputeDegradedStableId(SymbolKind.Class, "T:Bar", "A");
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void ComputeDegradedStableId_NullProject_Works()
    {
        var id = RecordMappers.ComputeDegradedStableId(SymbolKind.Class, "T:Foo", null);
        id.Should().StartWith("sym_");
    }

    // ── DetectLanguage ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Foo.cs", 1)]
    [InlineData("Bar.vb", 2)]
    [InlineData("Baz.fs", 3)]
    [InlineData("readme.txt", 0)]
    [InlineData("FOO.CS", 1)]  // case-insensitive
    public void DetectLanguage_AllExtensions(string path, short expected)
        => RecordMappers.DetectLanguage(path).Should().Be(expected);

    // ── SplitSha256 ──────────────────────────────────────────────────────────

    [Fact]
    public void SplitSha256_ValidHex_SplitsCorrectly()
    {
        var hash = "aabbccdd11223344" + new string('0', 48);
        var (high, low) = RecordMappers.SplitSha256(hash);
        high.Should().NotBe(0);
        low.Should().Be(0);
    }

    [Fact]
    public void SplitSha256_EmptyInput_ReturnsZeros()
    {
        var (high, low) = RecordMappers.SplitSha256("");
        high.Should().Be(0);
        low.Should().Be(0);
    }

    // ── SplitFactValue ───────────────────────────────────────────────────────

    [Fact]
    public void SplitFactValue_WithPipe_Splits()
    {
        var (primary, secondary) = RecordMappers.SplitFactValue("GET|/api/foo");
        primary.Should().Be("GET");
        secondary.Should().Be("/api/foo");
    }

    [Fact]
    public void SplitFactValue_NoPipe_PrimaryOnly()
    {
        var (primary, secondary) = RecordMappers.SplitFactValue("justvalue");
        primary.Should().Be("justvalue");
        secondary.Should().BeEmpty();
    }

    // ── BuildSymbolFlags ─────────────────────────────────────────────────────

    [Fact]
    public void BuildSymbolFlags_DecompiledCard_SetsBit7()
    {
        var card = CodeMap.Core.Models.SymbolCard.CreateMinimal(
            CodeMap.Core.Types.SymbolId.From("T:Foo"), "Foo", SymbolKind.Class,
            "class Foo", "Ns", CodeMap.Core.Types.FilePath.From("f.cs"), 1, 1, "public", Confidence.High)
            with { IsDecompiled = 1 };
        var flags = RecordMappers.BuildSymbolFlags(card);
        (flags & (1 << 7)).Should().NotBe(0);
    }

    [Fact]
    public void BuildSymbolFlags_NormalCard_ZeroFlags()
    {
        var card = CodeMap.Core.Models.SymbolCard.CreateMinimal(
            CodeMap.Core.Types.SymbolId.From("T:Foo"), "Foo", SymbolKind.Class,
            "class Foo", "Ns", CodeMap.Core.Types.FilePath.From("f.cs"), 1, 1, "public", Confidence.High);
        RecordMappers.BuildSymbolFlags(card).Should().Be(0);
    }
}
