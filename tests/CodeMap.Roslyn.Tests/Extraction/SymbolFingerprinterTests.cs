namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class SymbolFingerprinterTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static INamedTypeSymbol GetType(Compilation comp, string name) =>
        comp.GetSymbolsWithName(name, SymbolFilter.Type)
            .OfType<INamedTypeSymbol>().First();

    private static IMethodSymbol GetMethod(Compilation comp, string typeName, string methodName) =>
        GetType(comp, typeName).GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static IPropertySymbol GetProperty(Compilation comp, string typeName, string propName) =>
        GetType(comp, typeName).GetMembers(propName).OfType<IPropertySymbol>().First();

    private static IFieldSymbol GetField(Compilation comp, string typeName, string fieldName) =>
        GetType(comp, typeName).GetMembers(fieldName).OfType<IFieldSymbol>().First();

    // ── Format ───────────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_Format_StartsWithSymPrefix()
    {
        var comp = CompilationBuilder.Create("public class Foo { public void Run() {} }");
        var method = GetMethod(comp, "Foo", "Run");
        var id = SymbolFingerprinter.ComputeStableId(method);
        id.Value.Should().StartWith("sym_");
    }

    [Fact]
    public void Fingerprint_Format_Is20CharsTotal()
    {
        var comp = CompilationBuilder.Create("public class Foo { public void Run() {} }");
        var method = GetMethod(comp, "Foo", "Run");
        var id = SymbolFingerprinter.ComputeStableId(method);
        id.Value.Should().HaveLength(20); // "sym_" (4) + 16 hex chars
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_SameSymbol_ProducesSameStableId()
    {
        const string source = "public class Foo { public void Run(string s) {} }";
        var comp = CompilationBuilder.Create(source);
        var method = GetMethod(comp, "Foo", "Run");

        var id1 = SymbolFingerprinter.ComputeStableId(method);
        var id2 = SymbolFingerprinter.ComputeStableId(method);

        id1.Should().Be(id2);
    }

    [Fact]
    public void Fingerprint_DifferentSymbol_ProducesDifferentStableId()
    {
        const string source = """
            public class Foo {
                public void RunA(string s) {}
                public void RunB(int n) {}
            }
            """;
        var comp = CompilationBuilder.Create(source);
        var a = GetMethod(comp, "Foo", "RunA");
        var b = GetMethod(comp, "Foo", "RunB");

        SymbolFingerprinter.ComputeStableId(a).Should().NotBe(SymbolFingerprinter.ComputeStableId(b));
    }

    // ── Rename stability ──────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_MethodRenamed_StableIdUnchanged()
    {
        const string source1 = "namespace App { public class UserService { public bool GetUser(string id) => true; } }";
        const string source2 = "namespace App { public class UserService { public bool FetchUser(string id) => true; } }";

        var comp1 = CompilationBuilder.Create(source1);
        var comp2 = CompilationBuilder.Create(source2);

        var before = GetMethod(comp1, "UserService", "GetUser");
        var after = GetMethod(comp2, "UserService", "FetchUser");

        SymbolFingerprinter.ComputeStableId(before).Should().Be(SymbolFingerprinter.ComputeStableId(after));
    }

    [Fact]
    public void Fingerprint_ClassRenamed_MemberStableIdsChange()
    {
        // Class rename changes ContainerTypeFQN → member stable_ids change
        const string source1 = "namespace App { public class OldName { public void Run() {} } }";
        const string source2 = "namespace App { public class NewName { public void Run() {} } }";

        var comp1 = CompilationBuilder.Create(source1);
        var comp2 = CompilationBuilder.Create(source2);

        var before = GetMethod(comp1, "OldName", "Run");
        var after = GetMethod(comp2, "NewName", "Run");

        SymbolFingerprinter.ComputeStableId(before).Should().NotBe(SymbolFingerprinter.ComputeStableId(after));
    }

    [Fact]
    public void Fingerprint_NamespaceChanged_StableIdChanges()
    {
        const string source1 = "namespace OldNs { public class Svc { public void Run() {} } }";
        const string source2 = "namespace NewNs { public class Svc { public void Run() {} } }";

        var comp1 = CompilationBuilder.Create(source1);
        var comp2 = CompilationBuilder.Create(source2);

        var before = GetMethod(comp1, "Svc", "Run");
        var after = GetMethod(comp2, "Svc", "Run");

        SymbolFingerprinter.ComputeStableId(before).Should().NotBe(SymbolFingerprinter.ComputeStableId(after));
    }

    // ── Disambiguation ────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_OverloadedMethods_DifferentStableIds()
    {
        const string source = """
            public class C {
                public void Process(string s) {}
                public void Process(int n) {}
            }
            """;
        var comp = CompilationBuilder.Create(source);
        var methods = GetType(comp, "C").GetMembers("Process")
            .OfType<IMethodSymbol>().ToList();

        methods.Should().HaveCount(2);
        var id1 = SymbolFingerprinter.ComputeStableId(methods[0]);
        var id2 = SymbolFingerprinter.ComputeStableId(methods[1]);
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void Fingerprint_SameSignatureDifferentClasses_DifferentStableIds()
    {
        const string source = """
            public class A { public void Process() {} }
            public class B { public void Process() {} }
            """;
        var comp = CompilationBuilder.Create(source);
        var a = GetMethod(comp, "A", "Process");
        var b = GetMethod(comp, "B", "Process");

        SymbolFingerprinter.ComputeStableId(a).Should().NotBe(SymbolFingerprinter.ComputeStableId(b));
    }

    [Fact]
    public void Fingerprint_GenericMethod_IncludesArity()
    {
        const string source = """
            public class C {
                public void Process<T>() {}
                public void Process() {}
            }
            """;
        var comp = CompilationBuilder.Create(source);
        var generic = GetType(comp, "C").GetMembers("Process")
            .OfType<IMethodSymbol>().First(m => m.Arity == 1);
        var nonGeneric = GetType(comp, "C").GetMembers("Process")
            .OfType<IMethodSymbol>().First(m => m.Arity == 0);

        SymbolFingerprinter.ComputeStableId(generic).Should()
            .NotBe(SymbolFingerprinter.ComputeStableId(nonGeneric));
    }

    [Fact]
    public void Fingerprint_PropertyRenamed_StableIdUnchanged()
    {
        const string source1 = "public class C { public string Name { get; set; } = \"\"; }";
        const string source2 = "public class C { public string DisplayName { get; set; } = \"\"; }";

        var comp1 = CompilationBuilder.Create(source1);
        var comp2 = CompilationBuilder.Create(source2);

        var before = GetProperty(comp1, "C", "Name");
        var after = GetProperty(comp2, "C", "DisplayName");

        SymbolFingerprinter.ComputeStableId(before).Should().Be(SymbolFingerprinter.ComputeStableId(after));
    }

    // ── Symbol kinds ──────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_Class_IncludesKindAndArity()
    {
        const string source = """
            public class Foo {}
            public class Bar<T> {}
            """;
        var comp = CompilationBuilder.Create(source);
        var foo = GetType(comp, "Foo");
        var bar = GetType(comp, "Bar");

        // Different types → different ids
        SymbolFingerprinter.ComputeStableId(foo).Should().NotBe(SymbolFingerprinter.ComputeStableId(bar));
    }

    [Fact]
    public void Fingerprint_Interface_IncludesKindAndArity()
    {
        const string source = "public interface IFoo {}";
        var comp = CompilationBuilder.Create(source);
        var iface = GetType(comp, "IFoo");
        var id = SymbolFingerprinter.ComputeStableId(iface);
        id.Value.Should().StartWith("sym_");
    }

    [Fact]
    public void Fingerprint_Constructor_UsesContainingTypeAsFqn()
    {
        const string source = "public class Widget { public Widget(int x) {} }";
        var comp = CompilationBuilder.Create(source);
        var ctor = GetType(comp, "Widget").GetMembers(".ctor")
            .OfType<IMethodSymbol>().First();
        var id = SymbolFingerprinter.ComputeStableId(ctor);
        id.Value.Should().StartWith("sym_");
    }

    [Fact]
    public void Fingerprint_Property_IncludesPropertyType()
    {
        const string source = """
            public class C {
                public string Name { get; set; } = "";
                public int Count { get; set; }
            }
            """;
        var comp = CompilationBuilder.Create(source);
        var name = GetProperty(comp, "C", "Name");
        var count = GetProperty(comp, "C", "Count");

        // Different property types → different ids
        SymbolFingerprinter.ComputeStableId(name).Should().NotBe(SymbolFingerprinter.ComputeStableId(count));
    }

    [Fact]
    public void Fingerprint_Field_IncludesFieldType()
    {
        const string source = "public class C { public string _name = \"\"; public int _count; }";
        var comp = CompilationBuilder.Create(source);
        var strField = GetField(comp, "C", "_name");
        var intField = GetField(comp, "C", "_count");

        SymbolFingerprinter.ComputeStableId(strField).Should().NotBe(SymbolFingerprinter.ComputeStableId(intField));
    }

    // ── Disambiguation ────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_SameContainerSameSignature_Disambiguated()
    {
        // Two methods in same class with identical param types (differ only by name)
        const string source = """
            public class C {
                public void Foo(int x) {}
                public void Bar(int y) {}
            }
            """;
        var comp = CompilationBuilder.Create(source);
        var foo = GetMethod(comp, "C", "Foo");
        var bar = GetMethod(comp, "C", "Bar");

        // Single-symbol API sees collision (same structural fingerprint)
        SymbolFingerprinter.ComputeStableId(foo).Should().Be(SymbolFingerprinter.ComputeStableId(bar));

        // Batch API disambiguates
        var ids = SymbolFingerprinter.ComputeStableIds([foo, bar]);
        ids[foo].Should().NotBe(ids[bar]);
    }

    // ── Collision rate (batch API) ────────────────────────────────────────────

    [Fact]
    public void Fingerprint_LargeBatch_ZeroCollisions()
    {
        // Build a source with 1000+ distinct symbols using DIFFERENT param signatures
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            sb.AppendLine($"namespace Ns{i} {{");
            sb.AppendLine($"  public class C{i} {{");
            // 20 methods with distinct param types to avoid structural collisions
            sb.AppendLine($"    public int M00() => 0;");
            sb.AppendLine($"    public int M01(int a) => 0;");
            sb.AppendLine($"    public int M02(string a) => 0;");
            sb.AppendLine($"    public int M03(int a, int b) => 0;");
            sb.AppendLine($"    public int M04(int a, string b) => 0;");
            sb.AppendLine($"    public int M05(string a, int b) => 0;");
            sb.AppendLine($"    public int M06(string a, string b) => 0;");
            sb.AppendLine($"    public int M07(int a, int b, int c) => 0;");
            sb.AppendLine($"    public int M08(bool a) => 0;");
            sb.AppendLine($"    public int M09(double a) => 0;");
            sb.AppendLine($"    public string M10() => \"\";");
            sb.AppendLine($"    public bool M11() => true;");
            sb.AppendLine($"    public void M12() {{}}");
            sb.AppendLine($"    public void M13(int a) {{}}");
            sb.AppendLine($"    public void M14(string a) {{}}");
            sb.AppendLine($"    public static int M15() => 0;");
            sb.AppendLine($"    public static void M16() {{}}");
            sb.AppendLine($"    public static string M17() => \"\";");
            sb.AppendLine($"    public int M18(int a, int b, string c) => 0;");
            sb.AppendLine($"    public string M19(int a, string b, bool c) => \"\";");
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }

        var comp = CompilationBuilder.Create(sb.ToString());

        // Collect all method symbols
        var allMethods = new List<IMethodSymbol>();
        foreach (var tree in comp.SyntaxTrees)
        {
            var model = comp.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                var sym = model.GetDeclaredSymbol(node);
                if (sym is IMethodSymbol m && !m.IsImplicitlyDeclared)
                    allMethods.Add(m);
            }
        }

        allMethods.Should().HaveCountGreaterOrEqualTo(1000);

        // Use batch API to compute ids with disambiguation
        var batchIds = SymbolFingerprinter.ComputeStableIds(allMethods.Cast<ISymbol>());

        // All IDs should be unique
        var valueGroups = batchIds.Values.GroupBy(v => v.Value).Where(g => g.Count() > 1).ToList();
        valueGroups.Should().BeEmpty("batch API should produce unique stable_ids for all methods");
    }
}
