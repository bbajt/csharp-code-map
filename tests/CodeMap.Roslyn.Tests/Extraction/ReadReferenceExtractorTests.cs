namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

/// <summary>
/// Focused tests for RefKind.Read extraction (PHASE-02-04 / ADR-007 completion).
/// </summary>
public class ReadReferenceExtractorTests
{
    private static IReadOnlyList<Core.Interfaces.ExtractedReference> Extract(string source) =>
        ReferenceExtractor.ExtractAll(CompilationBuilder.Create(source), "");

    // ── Property reads ────────────────────────────────────────────────────────

    [Fact]
    public void Read_PropertyAccess_Classified()
    {
        const string source = """
            public class Order { public int Id { get; set; } }
            public class Service { public int GetId(Order o) { return o.Id; } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("Id"));
    }

    [Fact]
    public void Read_PropertyInExpression_Classified()
    {
        const string source = """
            public class Order { public int Total { get; set; } }
            public class Checker { public bool IsLarge(Order o) { return o.Total > 100; } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("Total"));
    }

    [Fact]
    public void Read_PropertyViaThis_Classified()
    {
        const string source = """
            public class Order {
                public int Total { get; set; }
                public bool IsLarge() { return Total > 100; }
            }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("Total"));
    }

    [Fact]
    public void Read_StaticPropertyAccess_Classified()
    {
        const string source = """
            public class Config { public static int MaxRetries { get; } = 3; }
            public class Client { public void Run() { var x = Config.MaxRetries; } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("MaxRetries"));
    }

    // ── Field reads ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_FieldAccess_Classified()
    {
        const string source = """
            public class Calc {
                private readonly int _value = 42;
                public int Get() { return _value; }
            }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("_value"));
    }

    [Fact]
    public void Read_ConstantFieldAccess_Classified()
    {
        const string source = """
            public class Constants { public const int Max = 10; }
            public class Worker { public bool Check(int n) { return n < Constants.Max; } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("Max"));
    }

    // ── Event reads ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_EventAccess_Classified()
    {
        const string source = """
            using System;
            public class Publisher {
                public event EventHandler? Changed;
                public void Raise() { Changed?.Invoke(this, EventArgs.Empty); }
            }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Read &&
            r.ToSymbol.Value.Contains("Changed"));
    }

    // ── Exclusions ────────────────────────────────────────────────────────────

    [Fact]
    public void Read_LocalVariableAccess_NotExtracted()
    {
        const string source = """
            public class Service {
                public int Compute() {
                    int local = 5;
                    return local + 1;
                }
            }
            """;
        var refs = Extract(source);
        // No Read refs — local variables are not tracked
        refs.Where(r => r.Kind == RefKind.Read).Should().BeEmpty();
    }

    [Fact]
    public void Read_ParameterAccess_NotExtracted()
    {
        const string source = """
            public class Service {
                public int Double(int x) { return x * 2; }
            }
            """;
        var refs = Extract(source);
        refs.Where(r => r.Kind == RefKind.Read).Should().BeEmpty();
    }

    [Fact]
    public void Read_MethodNameInCall_NotExtracted()
    {
        const string source = """
            public class A { public void Foo() {} }
            public class B { public void Bar() { new A().Foo(); } }
            """;
        var refs = Extract(source);
        // Only Call refs should appear, not Read
        refs.Should().Contain(r => r.Kind == RefKind.Call);
        refs.Where(r => r.Kind == RefKind.Read).Should().BeEmpty();
    }

    [Fact]
    public void Read_AssignmentLHS_IsWriteNotRead()
    {
        const string source = """
            public class Foo { public int X { get; set; } }
            public class Bar { public void Set(Foo f) { f.X = 5; } }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Write && r.ToSymbol.Value.Contains("X"));
        refs.Where(r => r.Kind == RefKind.Read && r.ToSymbol.Value.Contains("X")).Should().BeEmpty();
    }

    [Fact]
    public void Read_PropertyDeclarationSite_NotExtracted()
    {
        // The declaration itself should not produce a Read ref
        const string source = """
            public class Foo {
                public int Value { get; set; }
                public int Get() { return Value; }
            }
            """;
        var refs = Extract(source);
        // Should have exactly one Read ref (from Get()), not two
        refs.Where(r => r.Kind == RefKind.Read && r.ToSymbol.Value.Contains("Value"))
            .Should().HaveCount(1);
    }

    // ── Combined with other RefKinds ──────────────────────────────────────────

    [Fact]
    public void Read_SameSymbol_HasBothReadAndWriteRefs()
    {
        const string source = """
            public class Counter {
                private int _count = 0;
                public void Increment() { _count = _count + 1; }
            }
            """;
        var refs = Extract(source);
        refs.Should().Contain(r => r.Kind == RefKind.Write && r.ToSymbol.Value.Contains("_count"));
        refs.Should().Contain(r => r.Kind == RefKind.Read && r.ToSymbol.Value.Contains("_count"));
    }

    [Fact]
    public void Read_PropertyRead_ThenCall_BothExtracted()
    {
        const string source = """
            using System.Collections.Generic;
            public class Order {
                public List<string> Items { get; } = new();
            }
            public class Processor {
                public int Count(Order o) { return o.Items.Count; }
            }
            """;
        var refs = Extract(source);
        // Items property is read (MemberAccess "o.Items")
        refs.Should().Contain(r => r.Kind == RefKind.Read && r.ToSymbol.Value.Contains("Items"));
    }
}
