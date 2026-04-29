namespace CodeMap.Integration.Tests.Regression.Razor;

using CodeMap.Core.Enums;
using CodeMap.Roslyn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// End-to-end gate for MILESTONE-19 PHASE-19-01: indexes
/// <c>testdata/SampleBlazorSolution</c> via <see cref="RoslynCompiler"/>
/// and asserts that
///   (a) Razor backing classes appear,
///   (b) user-written @code methods appear,
///   (c) BuildRenderTree / _Imports.Execute / __Private* are filtered out,
///   (d) Blazor @page routes emit Route facts with the PAGE method token,
///   (e) the project compiles cleanly (no duplicate-type errors).
/// </summary>
[Trait("Category", "Integration")]
public class BlazorIndexingTests
{
    private static string BlazorSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleBlazorSolution", "SampleBlazorSolution.slnx"));

    private static RoslynCompiler CreateCompiler() =>
        new(NullLogger<RoslynCompiler>.Instance);

    [Fact]
    public async Task BlazorBackingClasses_AppearInIndex()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        result.Symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".Counter")
            && s.Kind == SymbolKind.Class);
        result.Symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".Home")
            && s.Kind == SymbolKind.Class);
        result.Symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".Weather")
            && s.Kind == SymbolKind.Class);
        result.Symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".MainLayout")
            && s.Kind == SymbolKind.Class);
        result.Symbols.Should().Contain(s => s.FullyQualifiedName.EndsWith(".Greeting")
            && s.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task UserWrittenAtCodeMethods_AppearInIndex()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        // Counter.IncrementCount lives inside @code { } and must not be filtered.
        result.Symbols.Should().Contain(
            s => s.Kind == SymbolKind.Method && s.FullyQualifiedName.Contains("IncrementCount"),
            because: "Counter has @code {{ private void IncrementCount() {{ ... }} }}");
    }

    [Fact]
    public async Task BuildRenderTree_NotIndexed()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        result.Symbols.Should().NotContain(
            s => s.Kind == SymbolKind.Method && s.FullyQualifiedName.Contains("BuildRenderTree"));
    }

    [Fact]
    public async Task ImportsSyntheticClass_NotIndexed()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        result.Symbols.Should().NotContain(
            s => s.Kind == SymbolKind.Class && s.FullyQualifiedName.Contains("._Imports"));
    }

    [Fact]
    public async Task BlazorPageRoutes_EmittedWithPageMethodToken()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        result.Facts.Should().NotBeNull();
        var pageRoutes = result.Facts!
            .Where(f => f.Kind == FactKind.Route && f.Value.StartsWith("PAGE ", StringComparison.Ordinal))
            .Select(f => f.Value)
            .ToList();

        pageRoutes.Should().Contain("PAGE /");
        pageRoutes.Should().Contain("PAGE /counter");
        pageRoutes.Should().Contain("PAGE /weather");
        pageRoutes.Should().Contain("PAGE /items/{Id:int}");
        pageRoutes.Should().Contain("PAGE /items/{Id:int}/details");
    }

    [Fact]
    public async Task NonPageComponents_DoNotEmitPageRoute()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        // MainLayout and Greeting have no @page directive.
        result.Facts.Should().NotBeNull();
        var values = result.Facts!.Where(f => f.Kind == FactKind.Route).Select(f => f.Value).ToList();
        values.Should().NotContain(v => v.StartsWith("PAGE ") &&
            (v.Contains("MainLayout") || v.Contains("Greeting")));
    }

    [Fact]
    public async Task SemanticLevel_IsFull_NoCompileErrors()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(BlazorSolutionPath);

        result.Stats.SemanticLevel.Should().Be(SemanticLevel.Full);
    }
}
