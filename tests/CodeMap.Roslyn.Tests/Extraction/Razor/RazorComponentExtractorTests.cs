namespace CodeMap.Roslyn.Tests.Extraction.Razor;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Verifies <see cref="RazorComponentExtractor"/> emits FactKind.RazorInject
/// and FactKind.RazorParameter facts for [Inject]/[Parameter] properties on
/// ComponentBase derivatives. Disambiguates from MVC's [Bind] / [FromQuery]
/// by checking the attribute's containing namespace.
/// </summary>
public class RazorComponentExtractorTests
{
    private const string ComponentStubs = """
        namespace Microsoft.AspNetCore.Components
        {
            public abstract class ComponentBase { }

            [System.AttributeUsage(System.AttributeTargets.Property)]
            public class InjectAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Property)]
            public class ParameterAttribute : System.Attribute { }
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
        return RazorComponentExtractor.ExtractAll(compilation, "/repo/");
    }

    [Fact]
    public void InjectProperty_EmitsRazorInjectFact()
    {
        const string component = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public interface IGreetingService { }

                public partial class Weather : ComponentBase
                {
                    [Inject]
                    public IGreetingService Greeter { get; set; } = null!;
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (component, "Weather_razor.g.cs"));

        facts.Should().Contain(f => f.Kind == FactKind.RazorInject
            && f.Value.Contains("Greeter")
            && f.Value.Contains("IGreetingService"));
    }

    [Fact]
    public void ParameterProperty_EmitsRazorParameterFact()
    {
        const string component = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Greeting : ComponentBase
                {
                    [Parameter]
                    public string Title { get; set; } = "Welcome";

                    [Parameter]
                    public string Message { get; set; } = "";
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (component, "Greeting_razor.g.cs"));

        var parameters = facts.Where(f => f.Kind == FactKind.RazorParameter).ToList();
        parameters.Should().HaveCount(2);
        parameters.Should().Contain(f => f.Value.Contains("Title"));
        parameters.Should().Contain(f => f.Value.Contains("Message"));
    }

    [Fact]
    public void NonComponentClass_PropertiesNotExtracted()
    {
        // [Inject] on a non-ComponentBase class is unusual but possible — we
        // only want Blazor-attached injections. Confirms scope by inheritance.
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public class Plain
                {
                    [Inject]
                    public string Foo { get; set; } = "";
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (source, "Plain.cs"));

        facts.Should().BeEmpty();
    }

    [Fact]
    public void InjectFromMvcNamespace_NotEmitted()
    {
        // ASP.NET MVC has its own [FromServices] / [Bind] / similar markers;
        // RazorComponentExtractor must not pick those up.
        const string mvcStubs = """
            namespace Microsoft.AspNetCore.Mvc
            {
                [System.AttributeUsage(System.AttributeTargets.Property)]
                public class FromServicesAttribute : System.Attribute { }
            }
            """;
        const string source = """
            using Microsoft.AspNetCore.Components;
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp
            {
                public class Service { }

                public partial class Counter : ComponentBase
                {
                    [FromServices]
                    public Service S { get; set; } = null!;
                }
            }
            """;

        var facts = Extract((ComponentStubs, "ComponentStubs.cs"), (mvcStubs, "MvcStubs.cs"), (source, "Counter_razor.g.cs"));

        facts.Should().NotContain(f => f.Kind == FactKind.RazorInject);
    }

    [Fact]
    public void IndirectComponentBaseDerivative_PropertiesExtracted()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public abstract class AppPageBase : ComponentBase { }

                public partial class HomePage : AppPageBase
                {
                    [Parameter]
                    public int Id { get; set; }
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (source, "HomePage_razor.g.cs"));

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.RazorParameter && f.Value.Contains("Id"));
    }

    [Fact]
    public void ValueFormat_IsNameColonType()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Card : ComponentBase
                {
                    [Parameter]
                    public string Title { get; set; } = "";
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (source, "Card_razor.g.cs"));

        var parameter = facts.Single(f => f.Kind == FactKind.RazorParameter);
        parameter.Value.Should().StartWith("Title: ");
        parameter.Value.Should().Contain("string");
    }

    [Fact]
    public void NoAttributeProperty_NotEmitted()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Counter : ComponentBase
                {
                    public int InternalState { get; set; }
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (source, "Counter_razor.g.cs"));

        facts.Should().BeEmpty();
    }

    [Fact]
    public void ConfidenceIsHigh()
    {
        const string source = """
            using Microsoft.AspNetCore.Components;
            namespace MyApp
            {
                public partial class Counter : ComponentBase
                {
                    [Parameter]
                    public int X { get; set; }
                }
            }
            """;

        var facts = Extract((ComponentStubs, "Stubs.cs"), (source, "Counter_razor.g.cs"));

        facts.Should().AllSatisfy(f => f.Confidence.Should().Be(Confidence.High));
    }
}
