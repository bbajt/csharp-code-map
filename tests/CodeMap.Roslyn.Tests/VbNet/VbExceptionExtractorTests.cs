namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbExceptionExtractorTests
{
    private static Compilation CreateCompilation(string source)
        => CompilationBuilder.CreateVb("TestVb", source);

    [Fact]
    public void ExtractsThrowStatement()
    {
        const string source = """
            Public Class Svc
                Public Sub Process(input As String)
                    If input Is Nothing Then
                        Throw New ArgumentNullException(NameOf(input))
                    End If
                End Sub
            End Class
            """;

        var facts = VbExceptionExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Exception &&
            f.Value.Contains("ArgumentNullException"));
    }

    [Fact]
    public void ExtractsRethrow()
    {
        const string source = """
            Public Class Svc
                Public Sub Process()
                    Try
                        DoWork()
                    Catch ex As Exception
                        Throw New InvalidOperationException("Error", ex)
                    End Try
                End Sub
                Private Sub DoWork()
                End Sub
            End Class
            """;

        var facts = VbExceptionExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f => f.Value.Contains("InvalidOperationException"));
    }

    [Fact]
    public void IgnoresCodeWithoutThrows()
    {
        const string source = """
            Public Class Foo
                Public Function Bar() As Integer
                    Return 42
                End Function
            End Class
            """;

        var facts = VbExceptionExtractor.ExtractAll(CreateCompilation(source), "");
        facts.Should().BeEmpty();
    }

    [Fact]
    public void BareRethrow_UsesSimpleTypeName_NotFullyQualified()
    {
        // MINOR-2 regression: bare Throw inside Catch ex As System.X.Y should extract "Y",
        // not "System.X.Y". Uses semanticModel.GetTypeInfo instead of syntax text.
        const string source = """
            Public Class Svc
                Public Sub Process()
                    Try
                        DoWork()
                    Catch ex As System.InvalidOperationException
                        Throw
                    End Try
                End Sub
                Private Sub DoWork() : End Sub
            End Class
            """;

        var facts = VbExceptionExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().Contain(f => f.Value == "InvalidOperationException|re-throw");
    }
}
