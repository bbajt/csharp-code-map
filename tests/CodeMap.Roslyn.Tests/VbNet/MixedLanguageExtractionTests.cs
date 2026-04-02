namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class MixedLanguageExtractionTests
{
    [Fact]
    public void ExtractSymbols_MixedSolution_BothLanguagesIndexed()
    {
        // Arrange: minimal C# and VB.NET compilations
        const string csSource = "namespace CsLib { public class CsClass { public void CsMethod() {} } }";
        const string vbSource = """
            Namespace VbLib
                Public Class VbClass
                    Public Sub VbMethod()
                    End Sub
                End Class
            End Namespace
            """;

        var csComp = CompilationBuilder.Create(csSource);
        var vbComp = CompilationBuilder.CreateVb("VbLib", vbSource);

        // Act
        var csSymbols = SymbolExtractor.ExtractAll(csComp, "CsLib");
        var vbSymbols = SymbolExtractor.ExtractAll(vbComp, "VbLib");

        // Assert
        csSymbols.Should().Contain(s => s.FullyQualifiedName.Contains("CsClass") && s.Kind == SymbolKind.Class);
        csSymbols.Should().Contain(s => s.FullyQualifiedName.Contains("CsMethod") && s.Kind == SymbolKind.Method);
        vbSymbols.Should().Contain(s => s.FullyQualifiedName.Contains("VbClass") && s.Kind == SymbolKind.Class);
        vbSymbols.Should().Contain(s => s.FullyQualifiedName.Contains("VbMethod") && s.Kind == SymbolKind.Method);
    }
}
