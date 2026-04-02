namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbConfigKeyExtractorTests
{
    // Minimal IConfiguration stub
    private const string ConfigStubs = """
        Public Interface IConfiguration
            Default Property Item(key As String) As String
            Function GetSection(key As String) As IConfiguration
            Function GetValue(Of T)(key As String) As T
        End Interface
        """;

    private static Compilation CreateCompilation(string vbSource)
        => CompilationBuilder.CreateVb("TestVb", ConfigStubs, vbSource);

    [Fact]
    public void ExtractsIndexerPattern()
    {
        const string source = """
            Public Class ConfigConsumer
                Private ReadOnly _config As IConfiguration
                Public Sub New(config As IConfiguration)
                    _config = config
                End Sub
                Public Function GetConn() As String
                    Return _config("ConnectionStrings:Default")
                End Function
            End Class
            """;

        var facts = VbConfigKeyExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Config &&
            f.Value.StartsWith("ConnectionStrings:Default|"));
    }

    [Fact]
    public void ExtractsGetValuePattern()
    {
        const string source = """
            Public Class ConfigConsumer
                Private ReadOnly _config As IConfiguration
                Public Sub New(config As IConfiguration)
                    _config = config
                End Sub
                Public Function GetRetries() As Integer
                    Return _config.GetValue(Of Integer)("App:MaxRetries")
                End Function
            End Class
            """;

        var facts = VbConfigKeyExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Value == "App:MaxRetries|GetValue");
    }

    [Fact]
    public void ExtractsGetSectionPattern()
    {
        const string source = """
            Public Class ConfigConsumer
                Private ReadOnly _config As IConfiguration
                Public Sub New(config As IConfiguration)
                    _config = config
                End Sub
                Public Function GetFeatures() As IConfiguration
                    Return _config.GetSection("Features")
                End Function
            End Class
            """;

        var facts = VbConfigKeyExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Value == "Features|GetSection");
    }

    [Fact]
    public void IgnoresNonConfigurationCalls()
    {
        const string source = """
            Public Class Foo
                Public Function Bar() As String
                    Return "hello"
                End Function
            End Class
            """;

        var facts = VbConfigKeyExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().BeEmpty();
    }
}
