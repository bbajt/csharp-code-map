namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class ReferenceExtractorTests
{
    private static IReadOnlyList<Core.Interfaces.ExtractedReference> Extract(string source) =>
        ReferenceExtractor.ExtractAll(CompilationBuilder.Create(source), "");

    [Fact]
    public void Extract_MethodCall_ProducesCallReference()
    {
        const string source = """
            public class A { public void Foo() {} }
            public class B { public void Bar() { new A().Foo(); } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Call);
    }

    [Fact]
    public void Extract_NewObject_ProducesInstantiateReference()
    {
        const string source = """
            public class Order {}
            public class Factory { public Order Create() { return new Order(); } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Instantiate);
    }

    [Fact]
    public void Extract_NewObject_TargetIsType_NotConstructor()
    {
        const string source = """
            public class Order {}
            public class Factory { public Order Create() { return new Order(); } }
            """;
        var refs = Extract(source);
        var instantiate = refs.Where(r => r.Kind == RefKind.Instantiate).ToList();
        // The ToSymbol should refer to the type (not contain ".ctor" or "#ctor")
        instantiate.Should().AllSatisfy(r =>
            r.ToSymbol.Value.Should().NotContain("#ctor"));
    }

    [Fact]
    public void Extract_PropertyAssignment_ProducesWriteReference()
    {
        const string source = """
            public class Foo { public int X { get; set; } }
            public class Bar { public void Set(Foo f) { f.X = 5; } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Write);
    }

    [Fact]
    public void Extract_OverrideMethod_ProducesOverrideReference()
    {
        const string source = """
            public class Base { public virtual void Greet() {} }
            public class Derived : Base { public override void Greet() {} }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Override);
    }

    [Fact]
    public void Extract_InterfaceImplementation_ProducesImplementationReference()
    {
        const string source = """
            public interface IGreeter { void Greet(); }
            public class Greeter : IGreeter { public void Greet() {} }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Implementation);
    }

    [Fact]
    public void Extract_Reference_HasCorrectLineNumbers()
    {
        const string source = """
            public class A { public void Foo() {} }
            public class B { public void Bar() { new A().Foo(); } }
            """;
        var refs = Extract(source);
        refs.Should().AllSatisfy(r =>
        {
            r.LineStart.Should().BeGreaterThanOrEqualTo(1);
            r.LineEnd.Should().BeGreaterThanOrEqualTo(r.LineStart);
        });
    }

    [Fact]
    public void Extract_NoReferencesInEmptyMethod_ReturnsEmpty()
    {
        const string source = "public class Foo { public void Bar() {} }";
        var refs = Extract(source);
        // No method calls, assignments, or new objects — only possible override/impl refs
        refs.Where(r => r.Kind == RefKind.Call || r.Kind == RefKind.Instantiate || r.Kind == RefKind.Write)
            .Should().BeEmpty();
    }

    [Fact]
    public void Extract_ChainedCalls_ProducesMultipleCallReferences()
    {
        const string source = """
            public class A { public A GetA() => this; public void Run() {} }
            public class B { public void Go() { new A().GetA().Run(); } }
            """;
        var refs = Extract(source);
        refs.Where(r => r.Kind == RefKind.Call).Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Extract_ExtensionMethodCall_ReferencesOriginalStaticDeclaration()
    {
        // The agent-feedback bug: `receiver.Ext()` was resolving to the reduced-extension
        // form, whose doc-comment ID didn't match the stored symbol_id of the declaration.
        // Callers were silently dropped — graph_callers returned 0 results.
        const string source = """
            public static class Extensions
            {
                public static void MapEndpoints(this string app) {}
            }
            public class Program
            {
                public void Run()
                {
                    "hello".MapEndpoints();
                }
            }
            """;
        var refs = Extract(source);
        var callRef = refs.Single(r => r.Kind == RefKind.Call);
        // Declaration symbol_id includes the `this` parameter type. Stored on the static
        // method — the call site must resolve to the same ID.
        callRef.ToSymbol.Value.Should().Be("M:Extensions.MapEndpoints(System.String)");
    }

    [Fact]
    public void Extract_GenericMethodCall_ReferencesOpenGenericDeclaration()
    {
        // Closed generic at call site (`Foo<int>()`) must map to the open generic declaration
        // (`M:...Foo``1`) that the baseline indexer stores.
        const string source = """
            public class Container
            {
                public T Get<T>() => default!;
            }
            public class User
            {
                public void Run() { new Container().Get<int>(); }
            }
            """;
        var refs = Extract(source);
        var callRef = refs.Single(r => r.Kind == RefKind.Call);
        callRef.ToSymbol.Value.Should().Be("M:Container.Get``1");
    }

    [Fact]
    public void Extract_GenericTypeInstantiation_ReferencesOpenGenericType()
    {
        const string source = """
            public class Box<T> { public Box(T value) {} }
            public class User
            {
                public void Run() { new Box<int>(42); }
            }
            """;
        var refs = Extract(source);
        var instRef = refs.Single(r => r.Kind == RefKind.Instantiate);
        instRef.ToSymbol.Value.Should().Be("T:Box`1");
    }

    // T2b regression tests — type-position identifiers are NOT classified as
    // Read refs. CodeMap captures type relationships via the type-relations
    // table; firing model.GetSymbolInfo on every typeof / generic-arg / base-
    // type identifier was the v2.5.1 perf regression on Blazorise.Docs.

    [Fact]
    public void Extract_TypeOf_DoesNotProduceReadReference()
    {
        const string source = """
            public class Foo {}
            public class User
            {
                public System.Type Run() => typeof(Foo);
            }
            """;
        var refs = Extract(source);
        refs.Should().NotContain(r => r.Kind == RefKind.Read && r.ToSymbol.Value == "T:Foo");
    }

    [Fact]
    public void Extract_GenericArgumentIdentifier_DoesNotProduceReadReference()
    {
        const string source = """
            public class Foo {}
            public class Container<T> { public T Item = default!; }
            public class User
            {
                public Container<Foo> Make() => new Container<Foo>();
            }
            """;
        var refs = Extract(source);
        refs.Should().NotContain(r => r.Kind == RefKind.Read && r.ToSymbol.Value == "T:Foo");
    }

    [Fact]
    public void Extract_BaseTypeIdentifier_DoesNotProduceReadReference()
    {
        const string source = """
            public class Animal {}
            public class Dog : Animal {}
            """;
        var refs = Extract(source);
        // Animal must not appear as a Read ref via the BaseTypeSyntax position.
        // Inheritance is captured in the type-relations table, not refs.
        refs.Should().NotContain(r => r.Kind == RefKind.Read && r.ToSymbol.Value == "T:Animal");
    }

    [Fact]
    public void Extract_QualifiedTypeName_DoesNotProduceReadReference()
    {
        const string source = """
            namespace N { public class Foo {} }
            public class User
            {
                public System.Type Run() => typeof(N.Foo);
            }
            """;
        var refs = Extract(source);
        refs.Should().NotContain(r => r.Kind == RefKind.Read && r.ToSymbol.Value == "T:N.Foo");
    }

    [Fact]
    public void Extract_AttributeIdentifier_DoesNotProduceReadReference()
    {
        const string source = """
            using System;
            public class FooAttribute : Attribute {}
            [Foo] public class Bar {}
            """;
        var refs = Extract(source);
        refs.Should().NotContain(r => r.Kind == RefKind.Read && r.ToSymbol.Value == "T:FooAttribute");
    }

    // T2a regression tests — auto-generated trees are skipped.

    [Fact]
    public void IsGeneratedFile_DotGCsSuffix_ReturnsTrue()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "public class X {}", path: "/project/obj/Release/net10.0/Foo.g.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratedFile_DesignerCsSuffix_ReturnsTrue()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "public class X {}", path: "/project/Foo.Designer.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratedFile_GeneratedCsSuffix_ReturnsTrue()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "public class X {}", path: "/project/Foo.Generated.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratedFile_PathContainsObj_ReturnsTrue()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "public class X {}", path: "/project/obj/Debug/net10.0/SomeFile.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratedFile_AutoGeneratedComment_ReturnsTrue()
    {
        const string source = """
            // <auto-generated>
            //     This code was generated by a tool.
            // </auto-generated>
            public class Foo {}
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: "/project/Foo.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratedFile_RegularFile_ReturnsFalse()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "public class X {}", path: "/project/src/Foo.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeFalse();
    }

    [Fact]
    public void IsGeneratedFile_GlobalUsings_ReturnsTrue()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "global using System;", path: "/project/obj/Debug/GlobalUsings.g.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }

    // PHASE-21-13 T01 (L-14): the Razor source generator's output IS the component's /
    // view's code (markup + @code), so it is not skipped even though it is a *.g.cs under
    // obj/ with an <auto-generated/> marker.

    [Theory]
    [InlineData("/project/obj/Debug/net10.0/generated/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Components_Pages_Counter_razor.g.cs")]
    [InlineData(@"C:\project\obj\Debug\net10.0\generated\RazorSourceGenerator\Components_Pages_Counter_RAZOR.G.CS")]
    public void IsGeneratedFile_RazorComponentTree_ReturnsFalse(string path)
    {
        const string source = """
            // <auto-generated/>
            #pragma warning disable 1591
            public partial class Counter {}
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: path);
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeFalse();
    }

    [Fact]
    public void IsGeneratedFile_RazorViewTree_ReturnsFalse()
    {
        const string source = """
            // <auto-generated/>
            public class Views_Home_Index {}
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source,
            path: "/project/obj/Debug/net10.0/generated/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Views_Home_Index_cshtml.g.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeFalse();
    }

    // PHASE-21-13 T01 (ADR-062): inside Razor generator trees, the generator's own
    // scaffolding (fields/properties declared at hidden, unmapped lines, e.g.
    // __tagHelperExecutionContext) is not a reference target; user code mapped back to the
    // .razor/.cshtml by #line is.

    private const string ShopModelSource = """
        public class ShopService { public string Greet() => "hi"; }
        """;

    private const string RazorViewTreePath =
        "/src/Web/obj/Debug/net10.0/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Views_Home_Index_cshtml.g.cs";

    /// <summary>Imitates the Razor generator's output shape: hidden scaffolding + #line-mapped user code.</summary>
    private const string RazorViewTreeSource = """
        // <auto-generated/>
        #pragma warning disable 1591
        namespace AspNetCoreGeneratedDocument
        {
            #line hidden
            internal sealed class Views_Home_Index
            {
                private object __tagHelperExecutionContext = new object();
                private static readonly string __tagHelperAttribute_0 = "x";
        #nullable restore
        #line (3,2)-(3,30) "/src/Web/Views/Home/Index.cshtml"
                private int visits;
        #line default
        #line hidden
                public void ExecuteAsync()
                {
                    var ctx = __tagHelperExecutionContext;
                    var attr = __tagHelperAttribute_0;
        #nullable restore
        #line (5,6)-(5,40) "/src/Web/Views/Home/Index.cshtml"
                    var g = new ShopService().Greet(); visits = visits + 1;
        #line default
        #line hidden
                }
            }
        }
        """;

    private static IReadOnlyList<Core.Interfaces.ExtractedReference> ExtractWithTree(string path, string treeSource)
    {
        var compilation = CompilationBuilder.Create(ShopModelSource)
            .AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(treeSource, path: path));
        return ReferenceExtractor.ExtractAll(compilation, "");
    }

    [Fact]
    public void ExtractAll_RazorViewTree_UserCodeReferencesKept()
    {
        var refs = ExtractWithTree(RazorViewTreePath, RazorViewTreeSource);

        refs.Should().Contain(r => r.ToSymbol.Value == "M:ShopService.Greet"
            && r.FromSymbol.Value == "M:AspNetCoreGeneratedDocument.Views_Home_Index.ExecuteAsync");
        refs.Should().Contain(r => r.ToSymbol.Value == "F:AspNetCoreGeneratedDocument.Views_Home_Index.visits",
            because: "a field declared in @code / @functions is mapped to the .cshtml by #line");
    }

    [Fact]
    public void ExtractAll_RazorViewTree_ScaffoldingTargetsDropped()
    {
        var refs = ExtractWithTree(RazorViewTreePath, RazorViewTreeSource);

        refs.Should().NotContain(r => r.ToSymbol.Value.Contains("__tagHelper", StringComparison.Ordinal),
            because: "the generator's own fields are declared at hidden lines, not written by anyone");
    }

    [Fact]
    public void ExtractAll_HiddenLineFieldInOrdinaryFile_Kept()
    {
        // The scaffolding rule applies to Razor generator trees only.
        var refs = ExtractWithTree("/src/Web/Plain.cs",
            RazorViewTreeSource.Replace("// <auto-generated/>", "// hand-written", StringComparison.Ordinal));

        refs.Should().Contain(r => r.ToSymbol.Value.EndsWith(".__tagHelperExecutionContext", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractAll_ReferenceToComponentType_Kept()
    {
        // A component class is declared at a hidden line of its _razor.g.cs, but a type is a
        // real target (new Counter(), a component tag), so types are never dropped.
        const string component = """
            // <auto-generated/>
            namespace App.Components
            {
                #line hidden
                public partial class Counter { }
            }
            """;
        const string user = """
            public class Nav { public object Target() => new App.Components.Counter(); }
            """;
        var compilation = CompilationBuilder.Create(user).AddSyntaxTrees(
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(component,
                path: "/src/Web/obj/Debug/net10.0/generated/RazorSourceGenerator/Components_Counter_razor.g.cs"));

        var refs = ReferenceExtractor.ExtractAll(compilation, "");

        refs.Should().Contain(r => r.ToSymbol.Value == "T:App.Components.Counter");
    }

    [Fact]
    public void IsGeneratedFile_RazorSuffixWithoutGeneratorShape_OtherGeneratorStillSkipped()
    {
        // Only the Razor generator's naming (_razor.g.cs / _cshtml.g.cs) is exempt.
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "public class X {}", path: "/project/obj/Debug/net10.0/generated/Other/razor.g.cs");
        ReferenceExtractor.IsGeneratedFile(tree).Should().BeTrue();
    }
}
