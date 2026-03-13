namespace CodeMap.Roslyn.Tests;

using CodeMap.Core.Enums;
using FluentAssertions;

public class SyntacticFallbackTests
{
    private static IReadOnlyList<Core.Models.SymbolCard> Extract(string source) =>
        SyntacticFallback.Extract([(FilePath: "Test.cs", Content: source)]);

    [Fact]
    public void Extract_ClassDeclaration_ReturnsSymbolWithLowConfidence()
    {
        var cards = Extract("public class OrderService {}");
        cards.Should().ContainSingle(c => c.FullyQualifiedName == "OrderService" && c.Confidence == Confidence.Low);
    }

    [Fact]
    public void Extract_MethodDeclaration_ReturnsSymbolWithLowConfidence()
    {
        var cards = Extract("public class Svc { public void Process() {} }");
        cards.Should().Contain(c => c.FullyQualifiedName == "Process" && c.Confidence == Confidence.Low);
    }

    [Fact]
    public void Extract_MultipleDeclarations_ReturnsAll()
    {
        const string source = """
            public class A {}
            public class B {}
            public class C { public void Run() {} }
            """;
        var cards = Extract(source);
        cards.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Extract_EmptyFile_ReturnsEmpty()
    {
        var cards = Extract("// just a comment");
        cards.Should().BeEmpty();
    }

    [Fact]
    public void Extract_SyntaxErrors_StillExtractsValidDeclarations()
    {
        // File with syntax error but also a valid class
        const string source = "public class Valid {} @@@@invalid";
        var cards = Extract(source);
        cards.Should().Contain(c => c.FullyQualifiedName == "Valid");
    }

    [Fact]
    public void Extract_AllSymbols_HaveLowConfidence()
    {
        var cards = Extract("public class Foo { public void Bar() {} }");
        cards.Should().AllSatisfy(c => c.Confidence.Should().Be(Confidence.Low));
    }
}
