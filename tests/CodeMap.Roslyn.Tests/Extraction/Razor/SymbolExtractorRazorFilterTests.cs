namespace CodeMap.Roslyn.Tests.Extraction.Razor;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Verifies that <see cref="SymbolExtractor"/> filters Razor source-generator
/// boilerplate (BuildRenderTree, _Imports synthetic class, __* nested attributes)
/// while preserving user-written members (e.g. @code methods).
/// </summary>
public class SymbolExtractorRazorFilterTests
{
    /// <summary>
    /// Stubs of the framework types the extractor inspects by name + inheritance.
    /// Using stubs (instead of FrameworkReference) keeps the test compilation
    /// fast and self-contained.
    /// </summary>
    private const string ComponentStubs = """
        namespace Microsoft.AspNetCore.Components
        {
            // Stub — no BuildRenderTree on the base so test assertions only see
            // the derivative's method, mirroring how the real framework type lives
            // in metadata (not source) during production indexing.
            public abstract class ComponentBase { }
        }
        """;

    private static IReadOnlyList<Core.Models.SymbolCard> Extract(params (string Source, string Path)[] files)
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
        return SymbolExtractor.ExtractAll(compilation, "TestProject");
    }

    [Fact]
    public void BuildRenderTree_OnComponentBaseDerivative_IsFiltered()
    {
        const string razor = """
            namespace MyApp.Components.Pages
            {
                public partial class Counter : Microsoft.AspNetCore.Components.ComponentBase
                {
                    protected void BuildRenderTree(object builder) { }
                    private int currentCount;
                    private void IncrementCount() { currentCount++; }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (razor, "Counter_razor.g.cs"));

        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("Counter") && c.Kind == CodeMap.Core.Enums.SymbolKind.Class);
        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("IncrementCount"));
        cards.Should().NotContain(c => c.FullyQualifiedName.EndsWith("BuildRenderTree"));
    }

    [Fact]
    public void BuildRenderTree_OnNonComponentClass_IsNotFiltered()
    {
        // A user class that happens to define BuildRenderTree without inheriting
        // ComponentBase must NOT be hidden.
        const string user = """
            namespace MyApp
            {
                public class CustomBuilder
                {
                    public void BuildRenderTree(object b) { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (user, "User.cs"));

        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("BuildRenderTree"));
    }

    [Fact]
    public void Imports_SyntheticClassInRazorGeneratedFile_IsFiltered()
    {
        const string imports = """
            namespace MyApp.Components
            {
                public class _Imports
                {
                    public static void Execute() { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (imports, "_Imports_razor.g.cs"));

        cards.Should().NotContain(c => c.FullyQualifiedName.EndsWith("_Imports"));
        cards.Should().NotContain(c => c.FullyQualifiedName.EndsWith("Execute"));
    }

    [Fact]
    public void Imports_UserClassNamed_Imports_OutsideRazorFile_IsKept()
    {
        // A user class literally named _Imports outside of a Razor SG file should
        // still be indexed. Filtering keys on file path, not name alone.
        const string user = """
            namespace MyApp.Util
            {
                public class _Imports
                {
                    public void Execute() { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (user, "Helpers.cs"));

        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("_Imports"));
    }

    [Fact]
    public void DoubleUnderscorePrefixedNestedType_OnComponentBase_IsFiltered()
    {
        const string razor = """
            namespace MyApp.Components.Pages
            {
                public partial class Counter : Microsoft.AspNetCore.Components.ComponentBase
                {
                    public class __PrivateComponentRenderModeAttribute : System.Attribute { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (razor, "Counter_razor.g.cs"));

        cards.Should().NotContain(c => c.FullyQualifiedName.Contains("__PrivateComponentRenderModeAttribute"));
        // The Counter class itself stays.
        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("Counter") && c.Kind == CodeMap.Core.Enums.SymbolKind.Class);
    }

    [Fact]
    public void DoubleUnderscoreNestedType_OnNonComponentClass_IsKept()
    {
        // Don't suppress double-underscore types nested in non-Blazor classes —
        // unusual but valid user code.
        const string user = """
            namespace MyApp
            {
                public class Container
                {
                    public class __Private { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (user, "User.cs"));

        cards.Should().Contain(c => c.FullyQualifiedName.Contains("__Private"));
    }

    [Fact]
    public void UserMethod_InComponentBaseDerivative_IsKept()
    {
        // Hand-written methods inside @code { } blocks (which the SG inlines into
        // the partial class) MUST NOT be filtered. They're the agent-relevant content.
        const string razor = """
            namespace MyApp.Components.Pages
            {
                public partial class Counter : Microsoft.AspNetCore.Components.ComponentBase
                {
                    protected void BuildRenderTree(object builder) { }
                    public async System.Threading.Tasks.Task LoadDataAsync() { }
                    public void Reset() { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (razor, "Counter_razor.g.cs"));

        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("LoadDataAsync"));
        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("Reset"));
        cards.Should().NotContain(c => c.FullyQualifiedName.EndsWith("BuildRenderTree"));
    }

    [Fact]
    public void IndirectComponentBaseDerivative_BuildRenderTreeFiltered()
    {
        // Two-step inheritance: user base class derives from ComponentBase, page
        // derives from user base. BuildRenderTree on the page must still be filtered.
        const string razor = """
            namespace MyApp.Components
            {
                public abstract class AppPageBase : Microsoft.AspNetCore.Components.ComponentBase { }

                public partial class HomePage : AppPageBase
                {
                    protected void BuildRenderTree(object b) { }
                }
            }
            """;

        var cards = Extract((ComponentStubs, "Stubs.cs"), (razor, "HomePage_razor.g.cs"));

        cards.Should().Contain(c => c.FullyQualifiedName.EndsWith("HomePage"));
        cards.Should().NotContain(c =>
            c.FullyQualifiedName.Contains("HomePage") && c.FullyQualifiedName.EndsWith("BuildRenderTree"));
    }
}
