namespace CodeMap.Roslyn.Tests.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.FSharp;
using FluentAssertions;
using Xunit;

public class FSharpTypeRelationMapperTests
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
    public void ExtractTypeRelations_FindsInterfaceImplementation()
    {
        var analyses = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);
        var (_, stableIdMap) = FSharpSymbolMapper.ExtractSymbols(analyses, "SampleFSharpApp", SolutionDir);
        var relations = FSharpTypeRelationMapper.ExtractTypeRelations(analyses, stableIdMap);

        // SimpleGreeter or FormalGreeter should implement IGreeter
        var greeterImpl = relations.FirstOrDefault(r =>
            r.TypeSymbolId.Value.Contains("Greeter") &&
            !r.TypeSymbolId.Value.Contains("IGreeter") &&
            r.RelationKind == TypeRelationKind.Interface &&
            r.RelatedSymbolId.Value.Contains("IGreeter"));

        greeterImpl.Should().NotBeNull("Greeter should implement IGreeter");
    }

    [Fact]
    public void ExtractTypeRelations_ExcludesSystemObject()
    {
        var analyses = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);
        var (_, stableIdMap) = FSharpSymbolMapper.ExtractSymbols(analyses, "SampleFSharpApp", SolutionDir);
        var relations = FSharpTypeRelationMapper.ExtractTypeRelations(analyses, stableIdMap);

        relations.Should().NotContain(r =>
            r.RelatedSymbolId.Value.Contains("System.Object"),
            "System.Object should be excluded from base types");
    }

    [Fact]
    public void ExtractTypeRelations_FindsOrderRepositoryInterface()
    {
        var analyses = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);
        var (_, stableIdMap) = FSharpSymbolMapper.ExtractSymbols(analyses, "SampleFSharpApp", SolutionDir);
        var relations = FSharpTypeRelationMapper.ExtractTypeRelations(analyses, stableIdMap);

        var repoImpl = relations.FirstOrDefault(r =>
            r.TypeSymbolId.Value.Contains("InMemoryOrderRepository") &&
            r.RelationKind == TypeRelationKind.Interface &&
            r.RelatedSymbolId.Value.Contains("IOrderRepository"));

        repoImpl.Should().NotBeNull("InMemoryOrderRepository should implement IOrderRepository");
    }
}
