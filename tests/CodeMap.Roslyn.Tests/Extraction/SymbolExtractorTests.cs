namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class SymbolExtractorTests
{
    private static IReadOnlyList<Core.Models.SymbolCard> Extract(string source) =>
        SymbolExtractor.ExtractAll(CompilationBuilder.Create(source), "TestProject");

    [Fact]
    public void ExtractAll_SimpleClass_ReturnsClassSymbol()
    {
        var cards = Extract("public class Order {}");
        cards.Should().ContainSingle(c => c.FullyQualifiedName.Contains("Order") && c.Kind == SymbolKind.Class);
    }

    [Fact]
    public void ExtractAll_SimpleMethod_ReturnsMethodSymbol()
    {
        var cards = Extract("public class Svc { public void Process() {} }");
        cards.Should().Contain(c => c.Kind == SymbolKind.Method && c.FullyQualifiedName.Contains("Process"));
    }

    [Fact]
    public void ExtractAll_ClassWithMembers_ReturnsTypeAndMembers()
    {
        var cards = Extract("public class Foo { public int X { get; set; } public void Bar() {} }");
        cards.Should().Contain(c => c.Kind == SymbolKind.Class);
        cards.Should().Contain(c => c.Kind == SymbolKind.Property);
        cards.Should().Contain(c => c.Kind == SymbolKind.Method);
    }

    [Fact]
    public void ExtractAll_MultipleClasses_ReturnsAll()
    {
        var cards = Extract("public class A {} public class B {} public class C {}");
        cards.Where(c => c.Kind == SymbolKind.Class).Should().HaveCount(3);
    }

    [Fact]
    public void Extract_SymbolId_IsNotEmpty()
    {
        var cards = Extract("public class Foo { public void Bar() {} }");
        cards.Should().AllSatisfy(c => c.SymbolId.Value.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void Extract_FullyQualifiedName_IncludesType()
    {
        var cards = Extract("namespace MyNs { public class Foo {} }");
        cards.Should().Contain(c => c.FullyQualifiedName.Contains("MyNs") && c.FullyQualifiedName.Contains("Foo"));
    }

    [Fact]
    public void Extract_Documentation_ExtractsSummaryText()
    {
        const string source = "/// <summary>My class.</summary>\npublic class Foo {}";
        var cards = Extract(source);
        cards.Should().Contain(c => c.Documentation == "My class.");
    }

    [Fact]
    public void Extract_Documentation_NullWhenNoXmlDoc()
    {
        var cards = Extract("public class Foo {}");
        cards.Should().Contain(c => c.Kind == SymbolKind.Class && c.Documentation == null);
    }

    [Fact]
    public void Extract_ContainingType_SetForMembers()
    {
        var cards = Extract("public class Foo { public void Bar() {} }");
        var methodCard = cards.Single(c => c.Kind == SymbolKind.Method);
        methodCard.ContainingType.Should().NotBeNull();
        methodCard.ContainingType.Should().Contain("Foo");
    }

    [Fact]
    public void Extract_ContainingType_NullForTopLevelTypes()
    {
        var cards = Extract("public class Foo {}");
        var classCard = cards.Single(c => c.Kind == SymbolKind.Class);
        classCard.ContainingType.Should().BeNull();
    }

    [Fact]
    public void Extract_SpanStartEnd_Are1Indexed()
    {
        var cards = Extract("public class Foo {}");
        var card = cards.Single(c => c.Kind == SymbolKind.Class);
        card.SpanStart.Should().BeGreaterThanOrEqualTo(1);
        card.SpanEnd.Should().BeGreaterThanOrEqualTo(card.SpanStart);
    }

    [Fact]
    public void Extract_Visibility_MapsCorrectly()
    {
        var cards = Extract("public class Foo { private int _x; internal void Bar() {} }");
        cards.Should().Contain(c => c.Visibility == "private");
        cards.Should().Contain(c => c.Visibility == "internal");
    }

    [Fact]
    public void Extract_RecordType_MapsToRecordKind()
    {
        var cards = Extract("public record MyRecord(int Id, string Name);");
        cards.Should().Contain(c => c.Kind == SymbolKind.Record);
    }

    [Fact]
    public void Extract_GenericClass_IncludesTypeParameters()
    {
        var cards = Extract("public class Repo<T> where T : class {}");
        var card = cards.Single(c => c.Kind == SymbolKind.Class);
        card.Signature.Should().Contain("T");
    }

    [Fact]
    public void Extract_Constructor_MapsToConstructorKind()
    {
        var cards = Extract("public class Foo { public Foo() {} }");
        cards.Should().Contain(c => c.Kind == SymbolKind.Constructor);
    }

    [Fact]
    public void Extract_Indexer_MapsToIndexerKind()
    {
        var cards = Extract("public class Foo { public int this[int i] { get => i; } }");
        cards.Should().Contain(c => c.Kind == SymbolKind.Indexer);
    }

    [Fact]
    public void Extract_ConstField_MapsToConstantKind()
    {
        var cards = Extract("public class Foo { public const int Max = 10; }");
        cards.Should().Contain(c => c.Kind == SymbolKind.Constant);
    }

    [Fact]
    public void Extract_SkipsImplicitlyDeclared()
    {
        // records auto-generate many implicit members — should not appear as extra cards
        var cards = Extract("public record Pt(int X, int Y);");
        // Should have the record type itself, but NOT compiler-generated backing fields
        var fields = cards.Where(c => c.Kind == SymbolKind.Field || c.Kind == SymbolKind.Constant);
        fields.Should().BeEmpty();
    }

    [Fact]
    public void Extract_SkipsPropertyAccessors()
    {
        // Property get_/set_ accessor methods should not appear as separate symbols
        var cards = Extract("public class Foo { public int X { get; set; } }");
        cards.Should().NotContain(c => c.Kind == SymbolKind.Method && c.FullyQualifiedName.Contains("get_"));
        cards.Should().NotContain(c => c.Kind == SymbolKind.Method && c.FullyQualifiedName.Contains("set_"));
    }

    [Fact]
    public void Extract_MethodWithThrow_ListsExceptionType()
    {
        const string source = """
            public class Svc {
                public void Process(string s) {
                    if (s == null) throw new System.ArgumentNullException(nameof(s));
                }
            }
            """;
        var cards = Extract(source);
        var method = cards.Single(c => c.Kind == SymbolKind.Method);
        method.ThrownExceptions.Should().Contain(e => e.Contains("ArgumentNullException"));
    }

    [Fact]
    public void Extract_MethodWithNoThrow_EmptyThrownExceptions()
    {
        var cards = Extract("public class Foo { public void Bar() {} }");
        var method = cards.Single(c => c.Kind == SymbolKind.Method);
        method.ThrownExceptions.Should().BeEmpty();
    }

    [Fact]
    public void Extract_AllSymbols_HaveHighConfidence()
    {
        var cards = Extract("public class Foo { public void Bar() {} }");
        cards.Should().AllSatisfy(c => c.Confidence.Should().Be(Confidence.High));
    }

    [Fact]
    public void Extract_Interface_SpanCoversFullBody()
    {
        const string source = """
            public interface IMyService
            {
                void MethodA();
                int MethodB(string s);
                Task MethodC();
                Task<int> MethodD(int x);
                bool MethodE();
            }
            """;
        var cards = Extract(source);
        var iface = cards.Single(c => c.Kind == SymbolKind.Interface);
        iface.SpanEnd.Should().BeGreaterThan(iface.SpanStart,
            "interface span must cover the full body, not just the identifier token");
    }

    [Fact]
    public void Extract_Class_SpanCoversFullBody()
    {
        const string source = """
            public class MyService
            {
                private int _field;
                public MyService() { }
                public void MethodA() { }
                public void MethodB() { }
                public void MethodC() { }
            }
            """;
        var cards = Extract(source);
        var cls = cards.Single(c => c.Kind == SymbolKind.Class);
        cls.SpanEnd.Should().BeGreaterThan(cls.SpanStart,
            "class span must cover the full body, not just the identifier token");
        cls.SpanEnd.Should().BeGreaterThanOrEqualTo(cls.SpanStart + 7,
            "class with 7 lines of members must span at least 7 lines");
    }

    [Fact]
    public void Extract_Struct_SpanCoversFullBody()
    {
        const string source = """
            public struct MyPoint
            {
                public int X { get; set; }
                public int Y { get; set; }
                public double Length => System.Math.Sqrt(X * X + Y * Y);
            }
            """;
        var cards = Extract(source);
        var st = cards.Single(c => c.Kind == SymbolKind.Struct);
        st.SpanEnd.Should().BeGreaterThan(st.SpanStart,
            "struct span must cover the full body");
    }

    [Fact]
    public void Extract_Method_SpanUnchanged()
    {
        // Method span should still work correctly after the type-symbol fix
        const string source = """
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                    var y = 2;
                }
            }
            """;
        var cards = Extract(source);
        var method = cards.Single(c => c.Kind == SymbolKind.Method);
        method.SpanEnd.Should().BeGreaterThanOrEqualTo(method.SpanStart,
            "method span must still be valid after type-symbol fix");
        method.SpanEnd.Should().BeGreaterThan(method.SpanStart,
            "multi-line method must have spanEnd > spanStart");
    }
}
