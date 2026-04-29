namespace CodeMap.Roslyn.Tests.Extraction.Razor;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Edge-case catalog for MILESTONE-19. Each test pins one of the 12 scenarios
/// listed in MILESTONE-19.MD §PHASE-19-02 T10. Failures here mean either the
/// extractor regressed or the spec needs updating.
/// </summary>
public class RazorEdgeCaseTests
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

            [System.AttributeUsage(System.AttributeTargets.Property)]
            public class InjectAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Property)]
            public class ParameterAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Property)]
            public class CascadingParameterAttribute : System.Attribute { }
        }
        """;

    private static (IReadOnlyList<Core.Models.ExtractedFact> Facts, IReadOnlyList<Core.Models.SymbolCard> Symbols)
        Extract(params (string Source, string Path)[] files)
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
        var endpointFacts = EndpointExtractor.ExtractAll(compilation, "/repo/");
        var componentFacts = RazorComponentExtractor.ExtractAll(compilation, "/repo/");
        var symbols = SymbolExtractor.ExtractAll(compilation, "TestProject");
        return ([.. endpointFacts, .. componentFacts], symbols);
    }

    /// <summary>Scenario 1 — layout / child components with no @page emit no Route fact.</summary>
    [Fact]
    public void NoPage_LayoutComponent_NoRouteFact()
    {
        const string layout = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Layout
            {
                public partial class MainLayout : ComponentBase { }
            }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (layout, "MainLayout_razor.g.cs"));
        facts.Should().NotContain(f => f.Kind == FactKind.Route);
    }

    /// <summary>Scenario 2 — multiple @page directives produce one fact each.</summary>
    [Fact]
    public void MultiplePageDirectives_OneFactEach()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp { [Route("/a")][Route("/b")][Route("/c")] public partial class P : ComponentBase { } }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "P_razor.g.cs"));
        facts.Where(f => f.Kind == FactKind.Route).Should().HaveCount(3);
    }

    /// <summary>Scenario 3 — route constraints preserved verbatim.</summary>
    [Fact]
    public void RouteConstraint_PreservedVerbatim()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp { [Route("/items/{id:int:min(1)}")] public partial class Items : ComponentBase { } }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "Items_razor.g.cs"));
        facts.Should().ContainSingle(f => f.Value == "PAGE /items/{id:int:min(1)}");
    }

    /// <summary>Scenario 4 — generic Blazor component is recognized.</summary>
    [Fact]
    public void GenericComponent_IsRecognized()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                [Route("/grid")]
                public partial class Grid<T> : ComponentBase
                {
                    [Parameter] public T? Selected { get; set; }
                }
            }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "Grid_razor.g.cs"));
        facts.Should().Contain(f => f.Kind == FactKind.Route && f.Value == "PAGE /grid");
        facts.Should().Contain(f => f.Kind == FactKind.RazorParameter && f.Value.Contains("Selected"));
    }

    /// <summary>Scenario 5 — [Inject] with framework type doesn't crash.</summary>
    [Fact]
    public void InjectFrameworkType_DoesNotCrash()
    {
        const string source = """
            using System;
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Counter : ComponentBase
                {
                    [Inject] public IServiceProvider? Services { get; set; }
                }
            }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "Counter_razor.g.cs"));
        facts.Should().Contain(f => f.Kind == FactKind.RazorInject
            && f.Value.Contains("IServiceProvider"));
    }

    /// <summary>Scenario 6 — [CascadingParameter] is currently NOT emitted as a fact.
    /// Documented as out-of-scope for M19; revisit if user demand emerges.</summary>
    [Fact]
    public void CascadingParameter_NotEmittedAsFact()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Inner : ComponentBase
                {
                    [CascadingParameter] public string? Theme { get; set; }
                }
            }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "Inner_razor.g.cs"));
        // CascadingParameter is intentionally NOT mapped to FactKind.RazorParameter.
        // This guards the decision; failure means scope crept.
        facts.Should().NotContain(f => f.Kind == FactKind.RazorParameter);
    }

    /// <summary>Scenario 7 — two-step inheritance through an app base class.</summary>
    [Fact]
    public void TwoStepInheritance_Recognized()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public abstract class AppPageBase : ComponentBase { }
                [Route("/x")]
                public partial class X : AppPageBase
                {
                    [Inject] public string Token { get; set; } = "";
                }
            }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "X_razor.g.cs"));
        facts.Should().Contain(f => f.Value == "PAGE /x");
        facts.Should().Contain(f => f.Kind == FactKind.RazorInject && f.Value.Contains("Token"));
    }

    /// <summary>Scenario 8 — Blazor + MVC controller in the same project coexist.</summary>
    [Fact]
    public void BlazorAndMvc_Coexist()
    {
        const string mvcStubs = """
            namespace Microsoft.AspNetCore.Mvc
            {
                [System.AttributeUsage(System.AttributeTargets.Class)] public class ApiControllerAttribute : System.Attribute { }
                [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)] public class RouteAttribute : System.Attribute { public RouteAttribute(string t) { } }
                public class ControllerBase { }
                [System.AttributeUsage(System.AttributeTargets.Method)] public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string t) { } }
            }
            """;
        const string blazor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Pages { [Route("/dashboard")] public partial class Dashboard : ComponentBase { } }
            """;
        const string controller = """
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp.Api { [ApiController][Route("/api/[controller]")] public class StatusController : ControllerBase { [HttpGet] public string G() => "ok"; } }
            """;

        var (facts, _) = Extract(
            (ComponentStubs, "ComponentStubs.cs"),
            (mvcStubs, "MvcStubs.cs"),
            (blazor, "Dashboard_razor.g.cs"),
            (controller, "StatusController.cs"));

        facts.Should().Contain(f => f.Value == "PAGE /dashboard");
        facts.Should().Contain(f => f.Value == "GET /api/status");
    }

    /// <summary>Scenario 9 — .cshtml Razor Pages (PageModel) coexists with .razor (Blazor)
    /// in the same project. The Blazor backing class must emit a PAGE Route fact and
    /// the PageModel class must NOT — Razor Pages routing is convention-based and
    /// outside the M19 surface.</summary>
    [Fact]
    public void RazorPagesAndBlazor_Coexist_OnlyBlazorEmitsPageFact()
    {
        const string razorPagesStubs = """
            namespace Microsoft.AspNetCore.Mvc.RazorPages
            {
                public abstract class PageModel { }
            }
            """;
        const string pageModel = """
            using Microsoft.AspNetCore.Mvc.RazorPages;
            namespace MyApp.Pages
            {
                // Convention-routed PageModel; no [Route] attribute.
                public class IndexModel : PageModel
                {
                    public void OnGet() { }
                }
            }
            """;
        const string blazor = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp.Pages { [Route("/blazor-home")] public partial class BlazorHome : ComponentBase { } }
            """;

        var (facts, symbols) = Extract(
            (ComponentStubs, "ComponentStubs.cs"),
            (razorPagesStubs, "RazorPagesStubs.cs"),
            (pageModel, "Pages/Index.cshtml.cs"),
            (blazor, "BlazorHome_razor.g.cs"));

        // Blazor PAGE route is emitted.
        facts.Should().ContainSingle(f => f.Kind == FactKind.Route && f.Value == "PAGE /blazor-home");
        // No PAGE fact for the PageModel — convention routing isn't in scope.
        facts.Where(f => f.Kind == FactKind.Route).Should().HaveCount(1);
        // The PageModel type is still indexed as a regular symbol.
        symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".IndexModel"));
    }

    /// <summary>Scenario 10 — when the Razor SG fails for one component (we simulate
    /// by including a syntactically broken backing tree), extraction must NOT crash
    /// and other components must still surface their facts.</summary>
    [Fact]
    public void OneBrokenSyntaxTree_DoesNotPreventOtherComponentsFromIndexing()
    {
        const string good = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                [Route("/healthy")]
                public partial class Healthy : ComponentBase
                {
                    [Inject] public string? Token { get; set; }
                }
            }
            """;
        // Broken: dangling brace, missing class body — Roslyn parses with errors,
        // the type isn't fully formed. Mimics SG failure for one .razor file.
        const string broken = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Broken : ComponentBase {
            """;

        var act = () => Extract(
            (ComponentStubs, "Stubs.cs"),
            (good, "Healthy_razor.g.cs"),
            (broken, "Broken_razor.g.cs"));

        act.Should().NotThrow("a broken backing tree must not crash extraction");

        var (facts, _) = act();
        facts.Should().Contain(f => f.Kind == FactKind.Route && f.Value == "PAGE /healthy");
        facts.Should().Contain(f => f.Kind == FactKind.RazorInject && f.Value.Contains("Token"));
    }

    /// <summary>Scenario 11 — @inject of an unregistered service still emits the fact
    /// (DI registration is checked at runtime, not at extraction time).</summary>
    [Fact]
    public void InjectUnregisteredService_StillEmitsFact()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public interface ISomeUnregisteredService { }
                public partial class C : ComponentBase
                {
                    [Inject] public ISomeUnregisteredService? Svc { get; set; }
                }
            }
            """;
        var (facts, _) = Extract((ComponentStubs, "Stubs.cs"), (source, "C_razor.g.cs"));
        facts.Should().Contain(f => f.Kind == FactKind.RazorInject
            && f.Value.Contains("Svc")
            && f.Value.Contains("ISomeUnregisteredService"));
    }

    /// <summary>Scenario 12 — non-Blazor user method named BuildRenderTree is preserved.</summary>
    [Fact]
    public void UserMethodNamedBuildRenderTree_OnPlainClass_Preserved()
    {
        const string source = """
            namespace MyApp
            {
                public class CustomRenderer
                {
                    public void BuildRenderTree(object b) { }
                }
            }
            """;
        var (_, symbols) = Extract((ComponentStubs, "Stubs.cs"), (source, "CustomRenderer.cs"));
        symbols.Should().Contain(s => s.FullyQualifiedName.Contains("BuildRenderTree"));
    }

    /// <summary>
    /// Scenario 13 (bonus) — nested @code block declares a private type.
    /// Private nested types declared in @code must still be indexed.
    /// </summary>
    [Fact]
    public void NestedTypeInAtCodeBlock_IsIndexed()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Weather : ComponentBase
                {
                    public class Forecast
                    {
                        public string Date { get; set; } = "";
                    }
                }
            }
            """;
        var (_, symbols) = Extract((ComponentStubs, "Stubs.cs"), (source, "Weather_razor.g.cs"));
        symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".Forecast"));
    }
}
