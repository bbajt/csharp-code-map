namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbMiddlewareExtractorTests
{
    private const string AppBuilderStub = """
        Public Interface IApplicationBuilder : End Interface

        Public Module AppBuilderExtensions
            <System.Runtime.CompilerServices.Extension>
            Public Function UseAuthentication(app As IApplicationBuilder) As IApplicationBuilder
                Return app
            End Function
            <System.Runtime.CompilerServices.Extension>
            Public Function UseAuthorization(app As IApplicationBuilder) As IApplicationBuilder
                Return app
            End Function
            <System.Runtime.CompilerServices.Extension>
            Public Function UseRouting(app As IApplicationBuilder) As IApplicationBuilder
                Return app
            End Function
        End Module
        """;

    private static Compilation CreateCompilation(string source)
        => CompilationBuilder.CreateVb("TestVb", AppBuilderStub, source);

    [Fact]
    public void ExtractsMiddlewarePipelineOrder()
    {
        const string source = """
            Public Module MiddlewareSetup
                Public Sub Configure(app As IApplicationBuilder)
                    app.UseAuthentication()
                    app.UseAuthorization()
                    app.UseRouting()
                End Sub
            End Module
            """;

        var facts = VbMiddlewareExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().HaveCount(3);
        facts[0].Value.Should().Contain("pos:1");
        facts[1].Value.Should().Contain("pos:2");
        facts[2].Value.Should().Contain("pos:3");
    }

    [Fact]
    public void IgnoresNonAppBuilderCalls()
    {
        const string source = """
            Public Class Foo
                Public Sub Bar()
                    Dim x As Integer = 42
                End Sub
            End Class
            """;

        var facts = VbMiddlewareExtractor.ExtractAll(CreateCompilation(source), "");
        facts.Should().BeEmpty();
    }
}
