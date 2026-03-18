namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class SymbolCardTests
{
    [Fact]
    public void CreateMinimal_ProducesCardWithEmptyCollections()
    {
        var card = SymbolCard.CreateMinimal(
            SymbolId.From("Ns.C.M"),
            "Ns.C.M",
            SymbolKind.Method,
            "void M()",
            "Ns",
            FilePath.From("src/C.cs"),
            10, 20, "public", Confidence.High);

        card.CallsTop.Should().BeEmpty();
        card.Facts.Should().BeEmpty();
        card.SideEffects.Should().BeEmpty();
        card.ThrownExceptions.Should().BeEmpty();
        card.Evidence.Should().BeEmpty();
        card.Documentation.Should().BeNull();
        card.ContainingType.Should().BeNull();
    }

    [Fact]
    public void CreateMinimal_SetsAllRequiredFields()
    {
        var card = SymbolCard.CreateMinimal(
            SymbolId.From("Ns.C.M"),
            "Ns.C.M",
            SymbolKind.Method,
            "void M()",
            "Ns",
            FilePath.From("src/C.cs"),
            10, 20, "public", Confidence.High,
            documentation: "Does things",
            containingType: "C");

        card.SymbolId.Should().Be(SymbolId.From("Ns.C.M"));
        card.FullyQualifiedName.Should().Be("Ns.C.M");
        card.Kind.Should().Be(SymbolKind.Method);
        card.Signature.Should().Be("void M()");
        card.Namespace.Should().Be("Ns");
        card.FilePath.Should().Be(FilePath.From("src/C.cs"));
        card.SpanStart.Should().Be(10);
        card.SpanEnd.Should().Be(20);
        card.Visibility.Should().Be("public");
        card.Confidence.Should().Be(Confidence.High);
        card.Documentation.Should().Be("Does things");
        card.ContainingType.Should().Be("C");
    }

    [Fact]
    public void SymbolCard_Has18Fields()
    {
        var props = typeof(SymbolCard).GetProperties();
        props.Should().HaveCount(19); // +1 for StableId (PHASE-03-01), +1 for IsDecompiled (PHASE-12-01)
    }
}
