namespace CodeMap.Roslyn.Tests.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.FSharp;
using FluentAssertions;
using Xunit;

public class FSharpSyntacticFallbackTests
{
    private static readonly string SampleFSharpDir = FindDir();
    private static string FsprojPath => Path.Combine(SampleFSharpDir, "SampleFSharpApp.fsproj");
    private static string SolutionDir => Path.GetDirectoryName(Path.GetDirectoryName(SampleFSharpDir))!;

    private static string FindDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "SampleFSharpSolution", "SampleFSharpApp");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find testdata/SampleFSharpSolution");
    }

    [Fact]
    public void ExtractAll_ReturnsSymbolsFromParseTree()
    {
        var (symbols, refs) = FSharpSyntacticFallback.ExtractAll(FsprojPath, SolutionDir);

        symbols.Should().NotBeEmpty("syntactic fallback should find modules and types");
    }

    [Fact]
    public void ExtractAll_FindsModules()
    {
        var (symbols, _) = FSharpSyntacticFallback.ExtractAll(FsprojPath, SolutionDir);

        var modules = symbols.Where(s => s.Kind == SymbolKind.Class &&
            s.SymbolId.Value.StartsWith("T:")).ToList();
        modules.Should().NotBeEmpty("should find module declarations");
    }

    [Fact]
    public void ExtractAll_FindsTypes()
    {
        var (symbols, _) = FSharpSyntacticFallback.ExtractAll(FsprojPath, SolutionDir);

        var types = symbols.Where(s => s.SymbolId.Value.StartsWith("T:")).ToList();
        types.Should().NotBeEmpty("should find type declarations");
    }

    [Fact]
    public void ExtractAll_FindsLetBindings()
    {
        var (symbols, _) = FSharpSyntacticFallback.ExtractAll(FsprojPath, SolutionDir);

        var methods = symbols.Where(s => s.Kind == SymbolKind.Method).ToList();
        methods.Should().NotBeEmpty("should find let-bound functions");
    }

    [Fact]
    public void ExtractAll_ReturnsEmptyRefs()
    {
        var (_, refs) = FSharpSyntacticFallback.ExtractAll(FsprojPath, SolutionDir);

        refs.Should().BeEmpty("syntactic fallback has no semantic info for refs");
    }

    [Fact]
    public void ExtractAll_SymbolsHaveLowConfidence()
    {
        var (symbols, _) = FSharpSyntacticFallback.ExtractAll(FsprojPath, SolutionDir);

        foreach (var s in symbols)
        {
            s.Confidence.Should().Be(Confidence.Low,
                "syntactic fallback symbols should have Low confidence");
        }
    }
}
