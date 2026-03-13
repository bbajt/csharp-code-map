namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class TypeRelationExtractorTests
{
    private static IReadOnlyList<Core.Models.ExtractedTypeRelation> Extract(string source) =>
        TypeRelationExtractor.ExtractAll(CompilationBuilder.Create(source));

    // ── Base type relations ─────────────────────────────────────────────────

    [Fact]
    public void Extract_ClassWithBaseType_ProducesBaseTypeRelation()
    {
        const string source = """
            public class Animal {}
            public class Dog : Animal {}
            """;

        var relations = Extract(source);

        relations.Should().ContainSingle(r =>
            r.RelationKind == TypeRelationKind.BaseType &&
            r.TypeSymbolId.Value.Contains("Dog") &&
            r.RelatedSymbolId.Value.Contains("Animal"));
    }

    [Fact]
    public void Extract_ClassExtendsObject_NoBaseTypeRelation()
    {
        // System.Object is the universal base class — must be excluded to avoid noise
        const string source = "public class Foo {}";

        var relations = Extract(source);

        relations.Where(r => r.RelationKind == TypeRelationKind.BaseType)
            .Should().BeEmpty();
    }

    // ── Interface relations ─────────────────────────────────────────────────

    [Fact]
    public void Extract_ClassWithInterface_ProducesInterfaceRelation()
    {
        const string source = """
            public interface IRunnable { void Run(); }
            public class Runner : IRunnable { public void Run() {} }
            """;

        var relations = Extract(source);

        relations.Should().ContainSingle(r =>
            r.RelationKind == TypeRelationKind.Interface &&
            r.TypeSymbolId.Value.Contains("Runner") &&
            r.RelatedSymbolId.Value.Contains("IRunnable"));
    }

    [Fact]
    public void Extract_ClassWithMultipleInterfaces_ProducesMultiple()
    {
        const string source = """
            public interface IFoo { void Foo(); }
            public interface IBar { void Bar(); }
            public class FooBar : IFoo, IBar
            {
                public void Foo() {}
                public void Bar() {}
            }
            """;

        var relations = Extract(source);

        var ifaceRelations = relations
            .Where(r =>
                r.RelationKind == TypeRelationKind.Interface &&
                r.TypeSymbolId.Value.Contains("FooBar"))
            .ToList();

        ifaceRelations.Should().HaveCount(2);
        ifaceRelations.Should().Contain(r => r.RelatedSymbolId.Value.Contains("IFoo"));
        ifaceRelations.Should().Contain(r => r.RelatedSymbolId.Value.Contains("IBar"));
    }

    // ── Interface hierarchy ─────────────────────────────────────────────────

    [Fact]
    public void Extract_Interface_NoBaseType()
    {
        // Interfaces have no BaseType in Roslyn — only Interfaces list
        const string source = "public interface IService { void Execute(); }";

        var relations = Extract(source);

        relations.Where(r =>
                r.RelationKind == TypeRelationKind.BaseType &&
                r.TypeSymbolId.Value.Contains("IService"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Extract_InterfaceExtendsInterface_ProducesInterfaceRelation()
    {
        const string source = """
            public interface IBase { void Base(); }
            public interface IDerived : IBase { void Extra(); }
            """;

        var relations = Extract(source);

        relations.Should().ContainSingle(r =>
            r.RelationKind == TypeRelationKind.Interface &&
            r.TypeSymbolId.Value.Contains("IDerived") &&
            r.RelatedSymbolId.Value.Contains("IBase"));
    }

    // ── Struct behaviour ────────────────────────────────────────────────────

    [Fact]
    public void Extract_Struct_NoBaseType()
    {
        // System.ValueType is the implicit base for all structs — must be excluded
        // just as System.Object is excluded for classes (both are universal, add noise)
        const string source = "public struct Point { public int X; public int Y; }";

        var relations = Extract(source);

        relations.Where(r =>
                r.RelationKind == TypeRelationKind.BaseType &&
                r.TypeSymbolId.Value.Contains("Point"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Extract_StructWithInterface_ProducesInterfaceRelation()
    {
        const string source = """
            public interface IEquatable { bool Equals(object obj); }
            public struct Vector : IEquatable
            {
                public bool Equals(object obj) => false;
            }
            """;

        var relations = Extract(source);

        relations.Should().ContainSingle(r =>
            r.RelationKind == TypeRelationKind.Interface &&
            r.TypeSymbolId.Value.Contains("Vector") &&
            r.RelatedSymbolId.Value.Contains("IEquatable"));
    }

    // ── Generic base types ──────────────────────────────────────────────────

    [Fact]
    public void Extract_GenericBaseType_CapturesSymbolId()
    {
        const string source = """
            public class Repository<T> {}
            public class OrderRepository : Repository<string> {}
            """;

        var relations = Extract(source);

        var baseRelation = relations.FirstOrDefault(r =>
            r.RelationKind == TypeRelationKind.BaseType &&
            r.TypeSymbolId.Value.Contains("OrderRepository"));

        baseRelation.Should().NotBeNull();
        baseRelation!.RelatedSymbolId.Value.Should().Contain("Repository");
    }

    // ── Enum types ──────────────────────────────────────────────────────────

    [Fact]
    public void Extract_EnumType_NoRelations()
    {
        // Enums extend System.Enum (which extends System.ValueType) — both universal, no relations
        const string source = """
            public enum Color { Red, Green, Blue }
            """;

        var relations = Extract(source);

        relations.Where(r => r.TypeSymbolId.Value.Contains("Color"))
            .Should().BeEmpty();
    }

    // ── DocumentationCommentId format ───────────────────────────────────────

    [Fact]
    public void Extract_ClassWithBaseType_SymbolIdUsesDocCommentFormat()
    {
        // SymbolId must use GetDocumentationCommentId() format: "T:Namespace.TypeName"
        const string source = """
            namespace MyApp
            {
                public class Base {}
                public class Derived : Base {}
            }
            """;

        var relations = Extract(source);

        var rel = relations.FirstOrDefault(r =>
            r.RelationKind == TypeRelationKind.BaseType &&
            r.TypeSymbolId.Value.Contains("Derived"));

        rel.Should().NotBeNull();
        rel!.TypeSymbolId.Value.Should().StartWith("T:");
        rel.RelatedSymbolId.Value.Should().StartWith("T:");
    }

    // ── DisplayName ─────────────────────────────────────────────────────────

    [Fact]
    public void Extract_ClassWithInterface_DisplayNameIsShortName()
    {
        const string source = """
            namespace MyApp
            {
                public interface IProcessor {}
                public class Processor : IProcessor {}
            }
            """;

        var relations = Extract(source);

        var rel = relations.FirstOrDefault(r =>
            r.RelationKind == TypeRelationKind.Interface &&
            r.TypeSymbolId.Value.Contains("Processor") &&
            !r.TypeSymbolId.Value.Contains("IProcessor"));

        rel.Should().NotBeNull();
        // MinimallyQualifiedFormat gives short name without full namespace
        rel!.DisplayName.Should().Be("IProcessor");
    }
}
