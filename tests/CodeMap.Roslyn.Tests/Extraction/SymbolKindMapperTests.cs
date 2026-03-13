namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using CmKind = CodeMap.Core.Enums.SymbolKind;

public class SymbolKindMapperTests
{
    private static INamedTypeSymbol GetType(string source, string typeName)
    {
        var compilation = CompilationBuilder.Create(source);
        return compilation.GetTypeByMetadataName(typeName)
            ?? throw new InvalidOperationException($"Type '{typeName}' not found");
    }

    private static ISymbol GetMember(string source, string typeName, string memberName)
    {
        var type = GetType(source, typeName);
        return type.GetMembers(memberName).First();
    }

    [Fact]
    public void Map_Class_ReturnsClass()
    {
        var symbol = GetType("public class Foo {}", "Foo");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Class);
    }

    [Fact]
    public void Map_Struct_ReturnsStruct()
    {
        var symbol = GetType("public struct Foo {}", "Foo");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Struct);
    }

    [Fact]
    public void Map_Interface_ReturnsInterface()
    {
        var symbol = GetType("public interface IFoo {}", "IFoo");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Interface);
    }

    [Fact]
    public void Map_Enum_ReturnsEnum()
    {
        var symbol = GetType("public enum Status { A, B }", "Status");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Enum);
    }

    [Fact]
    public void Map_Delegate_ReturnsDelegate()
    {
        var symbol = GetType("public delegate void MyDelegate(int x);", "MyDelegate");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Delegate);
    }

    [Fact]
    public void Map_RecordClass_ReturnsRecord()
    {
        var symbol = GetType("public record MyRecord(int Id);", "MyRecord");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Record);
    }

    [Fact]
    public void Map_RecordStruct_ReturnsRecord()
    {
        var symbol = GetType("public record struct Point(int X, int Y);", "Point");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Record);
    }

    [Fact]
    public void Map_Method_ReturnsMethod()
    {
        var symbol = GetMember("public class Foo { public void Bar() {} }", "Foo", "Bar");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Method);
    }

    [Fact]
    public void Map_Constructor_ReturnsConstructor()
    {
        var type = GetType("public class Foo { public Foo() {} }", "Foo");
        var ctor = type.GetMembers().OfType<IMethodSymbol>()
            .First(m => m.MethodKind == MethodKind.Constructor && !m.IsImplicitlyDeclared);
        SymbolKindMapper.Map(ctor).Should().Be(CmKind.Constructor);
    }

    [Fact]
    public void Map_Property_ReturnsProperty()
    {
        var symbol = GetMember("public class Foo { public int X { get; set; } }", "Foo", "X");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Property);
    }

    [Fact]
    public void Map_Indexer_ReturnsIndexer()
    {
        var type = GetType("public class Foo { public int this[int i] { get => i; } }", "Foo");
        var indexer = type.GetMembers().OfType<IPropertySymbol>().First(p => p.IsIndexer);
        SymbolKindMapper.Map(indexer).Should().Be(CmKind.Indexer);
    }

    [Fact]
    public void Map_Field_ReturnsField()
    {
        var symbol = GetMember("public class Foo { public int _x; }", "Foo", "_x");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Field);
    }

    [Fact]
    public void Map_ConstField_ReturnsConstant()
    {
        var symbol = GetMember("public class Foo { public const int MaxVal = 10; }", "Foo", "MaxVal");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Constant);
    }

    [Fact]
    public void Map_Event_ReturnsEvent()
    {
        var symbol = GetMember(
            "public class Foo { public event System.EventHandler? Changed; }", "Foo", "Changed");
        SymbolKindMapper.Map(symbol).Should().Be(CmKind.Event);
    }

    [Fact]
    public void Map_Operator_ReturnsOperator()
    {
        var type = GetType(
            "public class Foo { public static Foo operator +(Foo a, Foo b) => a; }", "Foo");
        var op = type.GetMembers().OfType<IMethodSymbol>()
            .First(m => m.MethodKind == MethodKind.UserDefinedOperator);
        SymbolKindMapper.Map(op).Should().Be(CmKind.Operator);
    }
}
