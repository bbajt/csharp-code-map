namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;

public class VbEndpointExtractorTests
{
    // Minimal stubs for attribute classes so the semantic model can resolve them
    private const string AttributeStubs = """
        Imports System

        <AttributeUsage(AttributeTargets.Class)>
        Public Class ApiControllerAttribute : Inherits Attribute : End Class

        <AttributeUsage(AttributeTargets.Class Or AttributeTargets.Method)>
        Public Class RouteAttribute : Inherits Attribute
            Public Sub New(template As String) : End Sub
        End Class

        <AttributeUsage(AttributeTargets.Method)>
        Public Class HttpGetAttribute : Inherits Attribute
            Public Sub New(Optional template As String = "") : End Sub
        End Class

        <AttributeUsage(AttributeTargets.Method)>
        Public Class HttpPostAttribute : Inherits Attribute
            Public Sub New(Optional template As String = "") : End Sub
        End Class

        <AttributeUsage(AttributeTargets.Method)>
        Public Class HttpPutAttribute : Inherits Attribute
            Public Sub New(Optional template As String = "") : End Sub
        End Class

        <AttributeUsage(AttributeTargets.Method)>
        Public Class HttpDeleteAttribute : Inherits Attribute
            Public Sub New(Optional template As String = "") : End Sub
        End Class

        <AttributeUsage(AttributeTargets.Method)>
        Public Class HttpPatchAttribute : Inherits Attribute
            Public Sub New(Optional template As String = "") : End Sub
        End Class

        Public Class Order : End Class
        """;

    private static Compilation CreateCompilation(string vbSource)
        => CompilationBuilder.CreateVb("TestVb", AttributeStubs, vbSource);

    [Fact]
    public void ExtractsGetEndpointFromController()
    {
        const string source = """
            <ApiController>
            <Route("api/[controller]")>
            Public Class OrdersController
                <HttpGet("{id}")>
                Public Function GetOrder(id As Integer) As Order
                    Return Nothing
                End Function
            End Class
            """;

        var facts = VbEndpointExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value.Contains("GET") &&
            f.Value.Contains("api/orders/{id}"));
    }

    [Fact]
    public void ExtractsPostEndpoint()
    {
        const string source = """
            <ApiController>
            <Route("api/orders")>
            Public Class OrdersController
                <HttpPost>
                Public Function CreateOrder(order As Object) As Boolean
                    Return True
                End Function
            End Class
            """;

        var facts = VbEndpointExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route && f.Value.Contains("POST"));
    }

    [Fact]
    public void ExtractsDeleteEndpoint()
    {
        const string source = """
            <ApiController>
            <Route("api/orders")>
            Public Class OrdersController
                <HttpDelete("{id}")>
                Public Async Function DeleteOrder(id As Integer) As System.Threading.Tasks.Task
                End Function
            End Class
            """;

        var facts = VbEndpointExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route && f.Value.Contains("DELETE"));
    }

    [Fact]
    public void IgnoresNonControllerClasses()
    {
        const string source = """
            Public Class NotAController
                Public Sub Foo()
                End Sub
            End Class
            """;

        var facts = VbEndpointExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().BeEmpty();
    }

    [Fact]
    public void DetectsControllerByNameSuffix()
    {
        const string source = """
            <Route("api/[controller]")>
            Public Class ProductsController
                <HttpGet>
                Public Function GetAll() As Object
                    Return Nothing
                End Function
            End Class
            """;

        var facts = VbEndpointExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value.Contains("GET") &&
            f.Value.Contains("products"));
    }
}
