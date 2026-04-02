namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbDiRegistrationExtractorTests
{
    // Minimal stubs so semantic model resolves IServiceCollection
    private const string DiStubs = """
        Public Interface IServiceCollection : End Interface
        Public Interface IOrderService : End Interface
        Public Class OrderService : Implements IOrderService : End Class

        Public Module ServiceCollectionExtensions
            <System.Runtime.CompilerServices.Extension>
            Public Function AddScoped(Of TService, TImpl)(services As IServiceCollection) As IServiceCollection
                Return services
            End Function

            <System.Runtime.CompilerServices.Extension>
            Public Function AddScoped(Of TService)(services As IServiceCollection) As IServiceCollection
                Return services
            End Function

            <System.Runtime.CompilerServices.Extension>
            Public Function AddSingleton(Of TService, TImpl)(services As IServiceCollection) As IServiceCollection
                Return services
            End Function

            <System.Runtime.CompilerServices.Extension>
            Public Function AddSingleton(Of TService)(services As IServiceCollection) As IServiceCollection
                Return services
            End Function

            <System.Runtime.CompilerServices.Extension>
            Public Function AddTransient(Of TService, TImpl)(services As IServiceCollection) As IServiceCollection
                Return services
            End Function
        End Module
        """;

    private static Compilation CreateCompilation(string vbSource)
        => CompilationBuilder.CreateVb("TestVb", DiStubs, vbSource);

    [Fact]
    public void ExtractsAddScopedWithServiceAndImpl()
    {
        const string source = """
            Public Module DiSetup
                <System.Runtime.CompilerServices.Extension>
                Public Function AddServices(services As IServiceCollection) As IServiceCollection
                    services.AddScoped(Of IOrderService, OrderService)()
                    Return services
                End Function
            End Module
            """;

        var facts = VbDiRegistrationExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.DiRegistration &&
            f.Value.Contains("IOrderService") &&
            f.Value.Contains("Scoped"));
    }

    [Fact]
    public void ExtractsAddSingletonSelfRegistration()
    {
        const string source = """
            Public Module DiSetup
                <System.Runtime.CompilerServices.Extension>
                Public Function AddServices(services As IServiceCollection) As IServiceCollection
                    services.AddSingleton(Of OrderService)()
                    Return services
                End Function
            End Module
            """;

        var facts = VbDiRegistrationExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.DiRegistration &&
            f.Value.Contains("Singleton"));
    }

    [Fact]
    public void ExtractsAddTransient()
    {
        const string source = """
            Public Module DiSetup
                <System.Runtime.CompilerServices.Extension>
                Public Function AddServices(services As IServiceCollection) As IServiceCollection
                    services.AddTransient(Of IOrderService, OrderService)()
                    Return services
                End Function
            End Module
            """;

        var facts = VbDiRegistrationExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.DiRegistration &&
            f.Value.Contains("Transient"));
    }

    [Fact]
    public void IgnoresUnrelatedMethodCalls()
    {
        const string source = """
            Public Class Foo
                Public Sub Bar()
                    Dim x As Integer = 42
                End Sub
            End Class
            """;

        var facts = VbDiRegistrationExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().BeEmpty();
    }
}
