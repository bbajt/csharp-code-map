namespace CodeMap.Roslyn.Tests.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.FSharp;
using FluentAssertions;
using Xunit;

public class FSharpReferenceMapperTests
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

    private static Core.Interfaces.ExtractedReference[] GetRefs()
    {
        var analyses = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);
        var (_, stableIdMap) = FSharpSymbolMapper.ExtractSymbols(analyses, "SampleFSharpApp", SolutionDir);
        var allSymbolIds = stableIdMap.Keys.ToHashSet(StringComparer.Ordinal);
        return FSharpReferenceMapper.ExtractReferences(analyses, SolutionDir, stableIdMap, allSymbolIds).ToArray();
    }

    [Fact]
    public void ExtractReferences_ReturnsNonEmpty()
    {
        var refs = GetRefs();
        refs.Should().NotBeEmpty("should find function calls and property reads");
    }

    [Fact]
    public void ExtractReferences_FindsCrossModuleCalls()
    {
        var refs = GetRefs();
        // OrderService.processOrder calls Calculator.add
        var crossModuleCall = refs.FirstOrDefault(r =>
            r.Kind == RefKind.Call &&
            r.ToSymbol.Value.Contains("Calculator.add"));

        crossModuleCall.Should().NotBeNull("processOrder → Calculator.add cross-module call should be detected");
    }

    [Fact]
    public void ExtractReferences_FindsCallsOrReads()
    {
        var refs = GetRefs();
        var callsOrReads = refs.Where(r => r.Kind == RefKind.Call || r.Kind == RefKind.Read).ToList();
        callsOrReads.Should().NotBeEmpty("should find function calls or property reads");
    }

    [Fact]
    public void ExtractReferences_NoDefinitionsIncluded()
    {
        var refs = GetRefs();
        // All refs should have non-empty ToSymbol
        foreach (var r in refs)
        {
            r.ToSymbol.Should().NotBe(Core.Types.SymbolId.Empty,
                "ToSymbol should not be empty");
        }
    }

    [Fact]
    public void ExtractReferences_FromSymbolPopulated()
    {
        var refs = GetRefs();
        var withFrom = refs.Where(r => r.FromSymbol != Core.Types.SymbolId.Empty).ToList();
        withFrom.Should().NotBeEmpty("most refs should have a FromSymbol (containing function)");
    }

    [Fact]
    public void ExtractReferences_FilePathsValid()
    {
        var refs = GetRefs();
        foreach (var r in refs)
        {
            r.FilePath.Value.Should().EndWith(".fs");
            r.FilePath.Value.Should().NotContain("\\");
        }
    }

    [Fact]
    public void ExtractReferences_LineNumbersPositive()
    {
        var refs = GetRefs();
        foreach (var r in refs)
        {
            r.LineStart.Should().BeGreaterThan(0);
        }
    }
}
