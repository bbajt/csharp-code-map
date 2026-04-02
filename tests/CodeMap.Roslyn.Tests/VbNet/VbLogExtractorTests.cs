namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbLogExtractorTests
{
    private const string LoggerStub = """
        Public Interface ILogger(Of T)
            Sub LogTrace(message As String, ParamArray args As Object())
            Sub LogDebug(message As String, ParamArray args As Object())
            Sub LogInformation(message As String, ParamArray args As Object())
            Sub LogWarning(message As String, ParamArray args As Object())
            Sub LogError(message As String, ParamArray args As Object())
            Sub LogCritical(message As String, ParamArray args As Object())
        End Interface
        """;

    private static Compilation CreateCompilation(string source)
        => CompilationBuilder.CreateVb("TestVb", LoggerStub, source);

    [Theory]
    [InlineData("LogInformation", "Information")]
    [InlineData("LogWarning", "Warning")]
    [InlineData("LogError", "Error")]
    public void ExtractsLogCall(string methodName, string expectedLevel)
    {
        var source = $$"""
            Public Class Svc
                Private ReadOnly _logger As ILogger(Of Svc)
                Public Sub New(l As ILogger(Of Svc)) : _logger = l : End Sub
                Public Sub Run()
                    _logger.{{methodName}}("Processing {Count} items", 5)
                End Sub
            End Class
            """;

        var facts = VbLogExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Log && f.Value.StartsWith(expectedLevel));
    }

    [Fact]
    public void IgnoresNonLoggerCalls()
    {
        const string source = """
            Public Class Foo
                Public Sub Bar()
                    Dim x As Integer = 42
                End Sub
            End Class
            """;

        var facts = VbLogExtractor.ExtractAll(CreateCompilation(source), "");
        facts.Should().BeEmpty();
    }
}
