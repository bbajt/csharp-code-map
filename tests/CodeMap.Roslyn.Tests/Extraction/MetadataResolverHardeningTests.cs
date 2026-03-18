namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Roslyn.Extraction;
using FluentAssertions;

/// <summary>
/// Unit tests for the three PHASE-12-05 hardening additions to MetadataResolver:
///   1. MaxDecompiledSourceChars size cap
///   2. DecompileTimeoutSeconds wall-clock timeout field
///   3. BelongsToType nested-type ref filter
/// </summary>
public class MetadataResolverHardeningTests
{
    // ─── MaxDecompiledSourceChars ─────────────────────────────────────────────

    [Fact]
    public void MaxDecompiledSourceChars_HasExpectedValue()
    {
        MetadataResolver.MaxDecompiledSourceChars.Should().Be(512_000);
    }

    // ─── DecompileTimeoutSeconds ──────────────────────────────────────────────

    [Fact]
    public void DecompileTimeoutSeconds_DefaultIs30()
    {
        // Use NullLogger / null store — we only inspect the field value, never call methods.
        var resolver = new MetadataResolver(
            null!,
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataResolver>.Instance);

        resolver.DecompileTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void DecompileTimeoutSeconds_IsSettable()
    {
        var resolver = new MetadataResolver(
            null!,
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataResolver>.Instance);

        resolver.DecompileTimeoutSeconds = 5;

        resolver.DecompileTimeoutSeconds.Should().Be(5);
    }

    // ─── BelongsToType ────────────────────────────────────────────────────────

    // All cases from the PHASE-12-05 test matrix.

    [Theory]
    // Direct member — no dot in member segment
    [InlineData("M:Outer.Inner.Method()",      "M:Outer.Inner.", "M:Outer.Inner.#", true)]
    // Direct member — no param list at all
    [InlineData("M:Outer.Inner.Method",        "M:Outer.Inner.", "M:Outer.Inner.#", true)]
    // Constructor — matched by ctorPrefix
    [InlineData("M:Outer.Inner.#ctor(System.String)", "M:Outer.Inner.", "M:Outer.Inner.#", true)]
    // Dot inside param list only — still a direct member
    [InlineData("M:Outer.Inner.Method(System.Collections.Generic.List{System.Int32})", "M:Outer.Inner.", "M:Outer.Inner.#", true)]
    // Nested type member — dot appears before '('
    [InlineData("M:Outer.Inner.Nested.Method()", "M:Outer.Inner.", "M:Outer.Inner.#", false)]
    // Sibling type — prefix mismatch
    [InlineData("M:Outer.InnerExtra.Method()",   "M:Outer.Inner.", "M:Outer.Inner.#", false)]
    // Completely unrelated type
    [InlineData("M:Other.Type.Method()",         "M:Outer.Inner.", "M:Outer.Inner.#", false)]
    public void BelongsToType_ReturnsExpected(
        string fromSymbolId, string prefix, string ctorPrefix, bool expected)
    {
        MetadataResolver.BelongsToType(fromSymbolId, prefix, ctorPrefix)
            .Should().Be(expected);
    }

    // ─── BelongsToType: ctor prefix shorthand ────────────────────────────────

    [Fact]
    public void BelongsToType_CtorWithNoArgs_ReturnsTrue()
    {
        // "#ctor()" — matched by ctorPrefix "M:Ns.Class.#"
        MetadataResolver.BelongsToType(
            "M:Ns.Class.#ctor()",
            "M:Ns.Class.",
            "M:Ns.Class.#")
            .Should().BeTrue();
    }

    [Fact]
    public void BelongsToType_CtorWithComplexParams_ReturnsTrue()
    {
        // "#ctor(System.Collections.Generic.Dictionary{System.String,System.Int32})"
        // — dot after '#' sits inside param list
        MetadataResolver.BelongsToType(
            "M:Ns.Class.#ctor(System.Collections.Generic.Dictionary{System.String,System.Int32})",
            "M:Ns.Class.",
            "M:Ns.Class.#")
            .Should().BeTrue();
    }

    // ─── BelongsToType: deeply nested ────────────────────────────────────────

    [Fact]
    public void BelongsToType_DeeplyNestedType_ReturnsFalse()
    {
        // "M:A.B.C.D.Method()" — three extra nesting levels
        MetadataResolver.BelongsToType(
            "M:A.B.C.D.Method()",
            "M:A.B.",
            "M:A.B.#")
            .Should().BeFalse();
    }

    // ─── BelongsToType: generic type with arity in name ──────────────────────

    [Fact]
    public void BelongsToType_GenericTypeArity_ReturnsTrue()
    {
        // "M:System.Collections.Generic.List`1.Add(System.Object)" — dot in param type
        MetadataResolver.BelongsToType(
            "M:System.Collections.Generic.List`1.Add(System.Object)",
            "M:System.Collections.Generic.List`1.",
            "M:System.Collections.Generic.List`1.#")
            .Should().BeTrue();
    }
}
