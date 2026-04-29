namespace CodeMap.Roslyn.Tests;

using FluentAssertions;

/// <summary>
/// Pins the marker used by <see cref="RoslynCompiler.IsPersistedRazorSgPath"/>
/// to detect on-disk Razor SG output emitted when a project sets
/// <c>&lt;EmitCompilerGeneratedFiles&gt;true&lt;/EmitCompilerGeneratedFiles&gt;</c>.
/// Without this guard the SG re-emits the same partial classes virtually during
/// compilation, producing duplicate-symbol errors. Validates the matching logic
/// covers Windows-style and Unix-style separators and rejects look-alikes.
/// </summary>
public class IsPersistedRazorSgPathTests
{
    [Theory]
    [InlineData(@"C:\src\App\Generated\Microsoft.CodeAnalysis.Razor.Compiler\Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator\Components\Pages\Counter_razor.g.cs")]
    [InlineData("/repo/App/Generated/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Components/Pages/Counter_razor.g.cs")]
    [InlineData(@"D:/work/Generated/Microsoft.CodeAnalysis.Razor.Compiler/sub/Greeting_razor.g.cs")]
    public void PersistedSgPath_ReturnsTrue(string path)
    {
        RoslynCompiler.IsPersistedRazorSgPath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\src\App\obj\Debug\net10.0\Microsoft.CodeAnalysis.Razor.Compiler\Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator\Counter_razor.g.cs")]
    public void DefaultObjOutput_NotMatched_ToAvoidStrippingDefaultBuild(string path)
    {
        // The default (EmitCompilerGeneratedFiles unset) build still places SG
        // output under obj/Debug/.../Microsoft.CodeAnalysis.Razor.Compiler/...
        // — but those files are NOT in project.Documents, so we never see them
        // here. Marker keys on the explicit /Generated/ root that the flag adds
        // so the default build path is left alone.
        RoslynCompiler.IsPersistedRazorSgPath(path).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\src\App\Components\Pages\Counter.razor")]
    [InlineData(@"C:\src\App\Components\Pages\Counter.cs")]
    [InlineData(@"C:\src\App\GenerationsLib\Foo.cs")]                                // 'Generations' substring shouldn't match
    [InlineData(@"C:\src\App\Generated\OtherGenerator\Foo.g.cs")]                    // different generator
    [InlineData(@"C:\src\App\Generated\Microsoft.CodeAnalysis.Razor.Compiler.Bak\Foo.g.cs")] // adjacent name
    public void NonSgPaths_ReturnFalse(string path)
    {
        RoslynCompiler.IsPersistedRazorSgPath(path).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_ReturnsFalse(string? path)
    {
        RoslynCompiler.IsPersistedRazorSgPath(path).Should().BeFalse();
    }
}
