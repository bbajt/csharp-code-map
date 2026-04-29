namespace CodeMap.Roslyn.Tests.Extraction.Razor;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Verifies that <see cref="EndpointExtractor"/> recognises Blazor
/// <c>[RouteAttribute]</c> applications on <c>ComponentBase</c> derivatives
/// and emits <see cref="FactKind.Route"/> facts with a <c>PAGE</c> method
/// token. Coexists with the existing controller and minimal-API passes.
/// </summary>
public class EndpointExtractorBlazorRouteTests
{
    private const string ComponentStubs = """
        namespace Microsoft.AspNetCore.Components
        {
            public abstract class ComponentBase { }

            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
            public class RouteAttribute : System.Attribute
            {
                public RouteAttribute(string template) { }
            }
        }
        """;

    private const string MvcStubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class ApiControllerAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)]
            public class RouteAttribute : System.Attribute
            {
                public RouteAttribute(string template) { }
            }

            public class ControllerBase { }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpGetAttribute : System.Attribute
            {
                public HttpGetAttribute() { }
                public HttpGetAttribute(string template) { }
            }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(params (string Source, string Path)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path)).ToArray();
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
        };
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return EndpointExtractor.ExtractAll(compilation, "/repo/");
    }

    [Fact]
    public void SinglePageDirective_EmitsPageRouteFact()
    {
        const string razor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Components.Pages
            {
                [Route("/counter")]
                public partial class Counter : ComponentBase { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (razor, "Counter_razor.g.cs"));

        facts.Should().Contain(f =>
            f.Kind == FactKind.Route && f.Value == "PAGE /counter");
    }

    [Fact]
    public void MultiplePageDirectives_EmitOneFactEach()
    {
        const string razor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                [Route("/items/{id:int}")]
                [Route("/items/{id:int}/details")]
                public partial class Items : ComponentBase { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (razor, "Items_razor.g.cs"));

        var pageRoutes = facts.Where(f => f.Value.StartsWith("PAGE ", StringComparison.Ordinal)).ToList();
        pageRoutes.Should().HaveCount(2);
        pageRoutes.Should().Contain(f => f.Value == "PAGE /items/{id:int}");
        pageRoutes.Should().Contain(f => f.Value == "PAGE /items/{id:int}/details");
    }

    [Fact]
    public void RouteConstraint_IsPreservedVerbatim()
    {
        const string razor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                [Route("/orders/{id:guid}")]
                public partial class Orders : ComponentBase { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (razor, "Orders_razor.g.cs"));

        facts.Should().ContainSingle(f => f.Value == "PAGE /orders/{id:guid}");
    }

    [Fact]
    public void NonComponentClassWithRouteAttribute_NotEmittedAsBlazorPage()
    {
        // A user POCO with a [Route] attribute (e.g. for a custom MVC binder)
        // must not be misclassified as a Blazor page. Match keys on ComponentBase.
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                [Route("/random")]
                public class NotAComponent { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (source, "NotAComponent.cs"));

        facts.Should().NotContain(f => f.Value.StartsWith("PAGE "));
    }

    [Fact]
    public void MvcRouteAttribute_OnControllerNotComponent_DoesNotEmitPageFact()
    {
        // The Blazor pass must filter by attribute namespace — MVC's [Route] lives
        // under Microsoft.AspNetCore.Mvc, Blazor's under Microsoft.AspNetCore.Components.
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp.Controllers
            {
                [ApiController]
                [Route("/api/orders")]
                public class OrdersController : ControllerBase
                {
                    [HttpGet]
                    public string List() => "[]";
                }
            }
            """;

        var facts = Extract((MvcStubs, "MvcStubs.cs"), (source, "OrdersController.cs"));

        // MVC pass still produces the GET route.
        facts.Should().Contain(f => f.Value == "GET /api/orders");
        // Blazor pass produces nothing.
        facts.Should().NotContain(f => f.Value.StartsWith("PAGE "));
    }

    [Fact]
    public void BlazorAndMvc_BothEmittedTogether()
    {
        const string blazor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Pages
            {
                [Route("/dashboard")]
                public partial class Dashboard : ComponentBase { }
            }
            """;
        const string mvc = """
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp.Api
            {
                [ApiController]
                [Route("/api/[controller]")]
                public class StatusController : ControllerBase
                {
                    [HttpGet]
                    public string Get() => "ok";
                }
            }
            """;

        var facts = Extract(
            (ComponentStubs, "ComponentStubs.cs"),
            (MvcStubs, "MvcStubs.cs"),
            (blazor, "Dashboard_razor.g.cs"),
            (mvc, "StatusController.cs"));

        facts.Should().Contain(f => f.Value == "PAGE /dashboard");
        facts.Should().Contain(f => f.Value == "GET /api/status");
    }

    [Fact]
    public void IndirectComponentBaseDerivative_EmitsPageRoute()
    {
        // App-defined intermediate base inheriting from ComponentBase — Blazor
        // pass must still recognise the page via inheritance chain walk.
        const string razor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public abstract class AppPageBase : ComponentBase { }

                [Route("/home")]
                public partial class HomePage : AppPageBase { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (razor, "HomePage_razor.g.cs"));

        facts.Should().ContainSingle(f => f.Value == "PAGE /home");
    }

    [Fact]
    public void OriginalRazorPath_FromPragmaChecksum_IsUsedAsFilePath()
    {
        // Razor SG output starts with #pragma checksum referencing the original
        // .razor file. EndpointExtractor should surface that path when present.
        const string razor = """
            #pragma checksum "C:/repo/MyApp/Components/Pages/Counter.razor" "{guid}" "abc123"
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Components.Pages
            {
                [Route("/counter")]
                public partial class Counter : ComponentBase { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (razor, "Counter_razor.g.cs"));

        facts.Should().ContainSingle(f =>
            f.Value == "PAGE /counter" &&
            f.FilePath.Value.EndsWith("Counter.razor", StringComparison.Ordinal));
    }

    [Fact]
    public void NoRazorAttribute_OnComponent_EmitsNoFact()
    {
        // Layout / child components without @page — no [RouteAttribute].
        // Must not emit a fact (no empty route).
        const string razor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Components.Layout
            {
                public partial class MainLayout : ComponentBase { }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (razor, "MainLayout_razor.g.cs"));

        facts.Should().NotContain(f => f.Value.StartsWith("PAGE "));
    }
}
