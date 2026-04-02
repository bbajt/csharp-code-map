namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using FluentAssertions;

[Collection("VbSampleSolution")]
public class VbSymbolExtractionTests(VbSampleSolutionFixture fixture)
{
    // --- Symbol presence ---

    [Fact]
    public void ExtractsOrderClass()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.EndsWith(".Order") && s.Kind == SymbolKind.Class);
        symbol.Should().NotBeNull();
        symbol!.Namespace.Should().Be("SampleVbApp.Models");
    }

    [Fact]
    public void ExtractsOrderStatusEnum()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("OrderStatus") && s.Kind == SymbolKind.Enum);
        symbol.Should().NotBeNull();
    }

    [Fact]
    public void ExtractsMoneyStructure()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("Money") && s.Kind == SymbolKind.Struct);
        symbol.Should().NotBeNull();
    }

    [Fact]
    public void ExtractsIEntityInterface()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("IEntity") && s.Kind == SymbolKind.Interface);
        symbol.Should().NotBeNull();
    }

    [Fact]
    public void ExtractsIOrderServiceInterface()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("IOrderService") && s.Kind == SymbolKind.Interface);
        symbol.Should().NotBeNull();
    }

    [Fact]
    public void ExtractsOrderServiceClass()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("OrderService") && s.Kind == SymbolKind.Class);
        symbol.Should().NotBeNull();
    }

    [Fact]
    public void ExtractsCalculatorModule_AsClass()
    {
        // VB.NET Modules compile to sealed static classes — Roslyn reports Kind=Class
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("Calculator"));
        symbol.Should().NotBeNull();
    }

    [Fact]
    public void ExtractsGetOrderAsyncMethod()
    {
        var symbol = fixture.Symbols.FirstOrDefault(s =>
            s.FullyQualifiedName.Contains("GetOrderAsync") && s.Kind == SymbolKind.Method);
        symbol.Should().NotBeNull();
    }

    // --- Method spans ---

    [Fact]
    public void MethodSpanIsNonZero()
    {
        // Find the OrderService implementation (multi-line body), not the interface declaration (one line)
        var method = fixture.Symbols
            .Where(s => s.FullyQualifiedName.Contains("SubmitOrderAsync") && s.Kind == SymbolKind.Method)
            .FirstOrDefault(s => s.SpanEnd > s.SpanStart);
        method.Should().NotBeNull("OrderService.SubmitOrderAsync implementation must have a multi-line body span");
        method!.SpanEnd.Should().BeGreaterThan(method.SpanStart);
    }

    // --- Reference extraction ---

    [Fact]
    public void ExtractsReferencesFromOrderService()
    {
        fixture.Refs.Should().NotBeEmpty("VB.NET refs must be extracted");
    }

    [Fact]
    public void ExtractsCallRefFromSubmitOrderAsync()
    {
        var callRefs = fixture.Refs.Where(r => r.Kind == RefKind.Call).ToList();
        callRefs.Should().NotBeEmpty();
    }

    [Fact]
    public void FilePathsAreRepoRelative()
    {
        fixture.Refs.Should().AllSatisfy(r =>
            r.FilePath.Value.Should().NotStartWith("/")
                .And.NotStartWith("\\")
                .And.NotContain(":\\"));
    }

    // --- SemanticLevel ---

    [Fact]
    public void SemanticLevelIsFull()
    {
        fixture.SemanticLevel.Should().Be(SemanticLevel.Full);
    }
}
