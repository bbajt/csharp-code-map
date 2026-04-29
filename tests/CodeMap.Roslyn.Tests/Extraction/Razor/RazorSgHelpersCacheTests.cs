namespace CodeMap.Roslyn.Tests.Extraction.Razor;

using CodeMap.Roslyn.Extraction.Razor;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Verifies <see cref="RazorSgHelpers.GetComponentBaseDerivatives"/> caches its
/// result per <see cref="Compilation"/> instance — the assembly walk runs at
/// most once even when multiple Razor extractors query the same compilation.
/// </summary>
public class RazorSgHelpersCacheTests
{
    private const string Source = """
        namespace Microsoft.AspNetCore.Components { public abstract class ComponentBase { } }
        namespace MyApp
        {
            public partial class Counter : Microsoft.AspNetCore.Components.ComponentBase { }
            public partial class Weather : Microsoft.AspNetCore.Components.ComponentBase { }
            public class NotAComponent { }
        }
        """;

    private static Compilation Compile()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
        };
        return CSharpCompilation.Create("Test", [tree], refs);
    }

    [Fact]
    public void GetComponentBaseDerivatives_FindsAllComponents()
    {
        var compilation = Compile();
        var components = RazorSgHelpers.GetComponentBaseDerivatives(compilation);

        components.Select(c => c.Name).Should().BeEquivalentTo("Counter", "Weather");
    }

    [Fact]
    public void GetComponentBaseDerivatives_ReturnsSameInstanceForSameCompilation()
    {
        var compilation = Compile();
        var first = RazorSgHelpers.GetComponentBaseDerivatives(compilation);
        var second = RazorSgHelpers.GetComponentBaseDerivatives(compilation);

        ReferenceEquals(first, second).Should().BeTrue("the cache must reuse the list across calls on the same Compilation");
    }

    [Fact]
    public void GetComponentBaseDerivatives_DistinctCompilations_DoNotShareCache()
    {
        var first = RazorSgHelpers.GetComponentBaseDerivatives(Compile());
        var second = RazorSgHelpers.GetComponentBaseDerivatives(Compile());

        ReferenceEquals(first, second).Should().BeFalse();
        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
    }
}
