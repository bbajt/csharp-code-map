namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Roslyn.Extraction;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;

public class VbLanguageDispatchTests
{
    private static Compilation CreateMinimalVbCompilation()
    {
        const string source = "Public Class Stub : End Class";
        return VisualBasicCompilation.Create("StubVb",
            [VisualBasicSyntaxTree.ParseText(source)],
            options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Theory]
    [InlineData("Endpoint")]
    [InlineData("DiRegistration")]
    [InlineData("ConfigKey")]
    [InlineData("DbTable")]
    [InlineData("Log")]
    [InlineData("Exception")]
    [InlineData("Middleware")]
    [InlineData("RetryPolicy")]
    public void ExtractAll_VbCompilation_ReturnsEmpty(string extractorName)
    {
        var comp = CreateMinimalVbCompilation();
        var facts = extractorName switch
        {
            "Endpoint"       => EndpointExtractor.ExtractAll(comp, ""),
            "DiRegistration" => DiRegistrationExtractor.ExtractAll(comp, ""),
            "ConfigKey"      => ConfigKeyExtractor.ExtractAll(comp, ""),
            "DbTable"        => DbTableExtractor.ExtractAll(comp, ""),
            "Log"            => LogExtractor.ExtractAll(comp, ""),
            "Exception"      => ExceptionExtractor.ExtractAll(comp, ""),
            "Middleware"     => MiddlewareExtractor.ExtractAll(comp, ""),
            "RetryPolicy"    => RetryPolicyExtractor.ExtractAll(comp, ""),
            _ => throw new ArgumentException(extractorName)
        };
        facts.Should().BeEmpty($"{extractorName} must return [] for VB.NET until implemented");
    }
}
