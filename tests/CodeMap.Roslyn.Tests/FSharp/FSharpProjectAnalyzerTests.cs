namespace CodeMap.Roslyn.Tests.FSharp;

using CodeMap.Roslyn.FSharp;
using FluentAssertions;
using Xunit;

public class FSharpProjectAnalyzerTests
{
    private static readonly string SampleFSharpDir = FindSampleFSharpDir();

    private static string FindSampleFSharpDir()
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

    private static string FsprojPath => Path.Combine(SampleFSharpDir, "SampleFSharpApp.fsproj");
    private static string SolutionDir => Path.GetDirectoryName(Path.GetDirectoryName(SampleFSharpDir))!;

    [Fact]
    public void GetSourceFiles_SampleFSharpApp_Returns4Files()
    {
        var files = FSharpProjectAnalyzer.GetSourceFiles(FsprojPath, SampleFSharpDir);

        files.Should().HaveCount(4);
        files.Select(Path.GetFileName).Should().ContainInOrder(
            "Models.fs", "Interfaces.fs", "Services.fs", "Patterns.fs");
    }

    [Fact]
    public void ResolveReferences_ReturnsFrameworkAndFSharpCore()
    {
        var refs = FSharpProjectAnalyzer.ResolveReferences(FsprojPath);

        refs.Should().NotBeEmpty();
        refs.Should().Contain(r => r.Contains("System.Runtime", StringComparison.OrdinalIgnoreCase));
        refs.Should().Contain(r => r.Contains("FSharp.Core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeProject_SampleFSharpApp_ReturnsResultsForAllFiles()
    {
        var results = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);

        results.Should().HaveCount(4);
        results.Select(r => Path.GetFileName(r.FilePath)).Should().ContainInOrder(
            "Models.fs", "Interfaces.fs", "Services.fs", "Patterns.fs");
    }

    [Fact]
    public void AnalyzeProject_SampleFSharpApp_AllFilesParsedSuccessfully()
    {
        var results = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);

        foreach (var r in results)
        {
            r.ParseResults.Should().NotBeNull($"{Path.GetFileName(r.FilePath)} should parse");
            r.ParseResults.ParseTree.Should().NotBeNull($"{Path.GetFileName(r.FilePath)} should have parse tree");
        }
    }

    [Fact]
    public void AnalyzeProject_SampleFSharpApp_AllFilesTypeChecked()
    {
        var results = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);

        foreach (var r in results)
        {
            r.CheckResults.Should().NotBeNull($"{Path.GetFileName(r.FilePath)} should type-check");
        }
    }

    [Fact]
    public void AnalyzeProject_SampleFSharpApp_EntitiesFound()
    {
        var results = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);

        // Collect all entities from all files
        var allEntities = results
            .Where(r => r.CheckResults != null)
            .SelectMany(r => r.CheckResults!.PartialAssemblySignature.Entities)
            .ToList();

        allEntities.Should().NotBeEmpty("should find F# entities");

        var entityNames = allEntities.Select(e => e.DisplayName).ToList();
        entityNames.Should().Contain("Calculator", "Calculator module should be found");
        entityNames.Should().Contain("OrderService", "OrderService module should be found");
    }

    [Fact]
    public void AnalyzeProject_SampleFSharpApp_SymbolUsesResolved()
    {
        var results = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);

        // At least some files should have check results with symbol uses
        var filesWithUses = results
            .Where(r => r.CheckResults != null)
            .Select(r => (File: Path.GetFileName(r.FilePath),
                          Uses: r.CheckResults!.GetAllUsesOfAllSymbolsInFile(null).Count()))
            .Where(x => x.Uses > 0)
            .ToList();

        filesWithUses.Should().NotBeEmpty("at least one F# file should have resolved symbol uses");
    }

    [Fact]
    public void AnalyzeProject_SampleFSharpApp_XmlDocSigAvailable()
    {
        var results = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);

        var entities = results
            .Where(r => r.CheckResults != null)
            .SelectMany(r => r.CheckResults!.PartialAssemblySignature.Entities)
            .ToList();

        // At least some entities should have XmlDocSig in T: format
        var withDocSig = entities.Where(e => e.XmlDocSig.StartsWith("T:")).ToList();
        withDocSig.Should().NotBeEmpty("F# entities should produce T: XmlDocSig");
    }
}
