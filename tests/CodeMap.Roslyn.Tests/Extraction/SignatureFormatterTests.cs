namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class SignatureFormatterTests
{
    private static ISymbol GetMember(string source, string typeName, string memberName)
    {
        var compilation = CompilationBuilder.Create(source);
        var type = compilation.GetTypeByMetadataName(typeName)!;
        return type.GetMembers(memberName).First();
    }

    private static INamedTypeSymbol GetType(string source, string typeName)
    {
        var compilation = CompilationBuilder.Create(source);
        return compilation.GetTypeByMetadataName(typeName)!;
    }

    [Fact]
    public void Format_VoidMethodNoParams_ContainsMethodName()
    {
        var sym = GetMember("public class Foo { public void DoWork() {} }", "Foo", "DoWork");
        var result = SignatureFormatter.Format(sym);
        result.Should().Contain("DoWork");
        result.Should().Contain("void");
    }

    [Fact]
    public void Format_MethodWithParams_ContainsParamTypes()
    {
        var sym = GetMember("public class Foo { public int Add(int a, int b) => a + b; }", "Foo", "Add");
        var result = SignatureFormatter.Format(sym);
        result.Should().Contain("Add");
        result.Should().Contain("int");
    }

    [Fact]
    public void Format_GenericMethod_IncludesTypeParameter()
    {
        var sym = GetMember(
            "public class Foo { public T Get<T>(string key) where T : class => default!; }",
            "Foo", "Get");
        var result = SignatureFormatter.Format(sym);
        result.Should().Contain("Get");
        result.Should().Contain("<T>");
    }

    [Fact]
    public void Format_Property_ContainsPropertyName()
    {
        var sym = GetMember(
            "public class Foo { public string Name { get; set; } = string.Empty; }", "Foo", "Name");
        var result = SignatureFormatter.Format(sym);
        result.Should().Contain("Name");
    }

    [Fact]
    public void Format_ClassSignature_ContainsClassName()
    {
        var sym = GetType("public class OrderService {}", "OrderService");
        var result = SignatureFormatter.Format(sym);
        result.Should().Contain("OrderService");
    }

    [Fact]
    public void Format_GenericClassSignature_IncludesTypeParameter()
    {
        var sym = GetType("public class Repo<T> where T : class {}", "Repo`1");
        var result = SignatureFormatter.Format(sym);
        result.Should().Contain("Repo");
        result.Should().Contain("<T>");
    }
}
