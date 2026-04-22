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
        // Callers were silently dropped — graph.callers returned 0 results.
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
}
