namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbRetryPolicyExtractorTests
{
    private static Compilation CreateCompilation(string source)
        => CompilationBuilder.CreateVb("TestVb", source);

    [Fact]
    public void ExtractsPollyRetry()
    {
        const string source = """
            Public Class Setup
                Public Sub Configure()
                    Policy.Handle(Of Exception)().RetryAsync(3)
                End Sub
            End Class
            Public Class Policy
                Public Shared Function Handle(Of T)() As PolicyBuilder
                    Return New PolicyBuilder()
                End Function
            End Class
            Public Class PolicyBuilder
                Public Function RetryAsync(count As Integer) As PolicyBuilder
                    Return Me
                End Function
            End Class
            """;

        var facts = VbRetryPolicyExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.RetryPolicy &&
            f.Value.Contains("Polly") &&
            f.Value.Contains("retry:3"));
    }

    [Fact]
    public void IgnoresUnrelatedCalls()
    {
        const string source = """
            Public Class Foo
                Public Sub Bar() : End Sub
            End Class
            """;

        var facts = VbRetryPolicyExtractor.ExtractAll(CreateCompilation(source), "");
        facts.Should().BeEmpty();
    }
}
