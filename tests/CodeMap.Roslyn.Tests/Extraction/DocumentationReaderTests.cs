namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class DocumentationReaderTests
{
    [Fact]
    public void GetSummary_WithXmlDocComment_ReturnsSummaryText()
    {
        const string source = """
            /// <summary>Processes an order for a customer.</summary>
            public class OrderProcessor {}
            """;
        var compilation = CompilationBuilder.Create(source);
        var type = compilation.GetTypeByMetadataName("OrderProcessor")!;

        var result = DocumentationReader.GetSummary(type);

        result.Should().Be("Processes an order for a customer.");
    }

    [Fact]
    public void GetSummary_WithMultilineXmlDoc_NormalizesWhitespace()
    {
        const string source = """
            /// <summary>
            /// Processes an order
            /// for a customer.
            /// </summary>
            public class OrderProcessor {}
            """;
        var compilation = CompilationBuilder.Create(source);
        var type = compilation.GetTypeByMetadataName("OrderProcessor")!;

        var result = DocumentationReader.GetSummary(type);

        result.Should().Be("Processes an order for a customer.");
    }

    [Fact]
    public void GetSummary_NoXmlDoc_ReturnsNull()
    {
        const string source = "public class NoDoc {}";
        var compilation = CompilationBuilder.Create(source);
        var type = compilation.GetTypeByMetadataName("NoDoc")!;

        var result = DocumentationReader.GetSummary(type);

        result.Should().BeNull();
    }

    [Fact]
    public void GetSummary_XmlDocWithOtherElements_ExtractsOnlySummary()
    {
        const string source = """
            /// <summary>My summary.</summary>
            /// <param name="x">A param.</param>
            /// <returns>A result.</returns>
            public class Foo {}
            """;
        var compilation = CompilationBuilder.Create(source);
        var type = compilation.GetTypeByMetadataName("Foo")!;

        var result = DocumentationReader.GetSummary(type);

        result.Should().Be("My summary.");
    }
}
