namespace CodeMap.Roslyn.Tests.FSharp;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.FSharp;
using FluentAssertions;
using Xunit;

public class FSharpSymbolMapperTests
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

    private static (IReadOnlyList<Core.Models.SymbolCard> Cards, IReadOnlyDictionary<string, Core.Types.StableId> StableIdMap) GetSymbols()
    {
        var analyses = FSharpProjectAnalyzer.AnalyzeProject(FsprojPath, SolutionDir);
        return FSharpSymbolMapper.ExtractSymbols(analyses, "SampleFSharpApp", SolutionDir);
    }

    [Fact]
    public void ExtractSymbols_ReturnsNonEmpty()
    {
        var (cards, _) = GetSymbols();
        cards.Should().NotBeEmpty();
        cards.Count.Should().BeGreaterThan(20, "should find types + members");
    }

    [Fact]
    public void ExtractSymbols_FindsModuleAsClass()
    {
        var (cards, _) = GetSymbols();
        var calculator = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("Calculator")
            && c.Kind == SymbolKind.Class);
        calculator.Should().NotBeNull("Calculator module should map to Class");
    }

    [Fact]
    public void ExtractSymbols_FindsRecordType()
    {
        var (cards, _) = GetSymbols();
        var order = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("Order")
            && c.Kind == SymbolKind.Record
            && !c.SymbolId.Value.Contains("Status")
            && !c.SymbolId.Value.Contains("Item")
            && !c.SymbolId.Value.Contains("Service"));
        order.Should().NotBeNull("Order record should map to Record kind");
    }

    [Fact]
    public void ExtractSymbols_FindsInterface()
    {
        var (cards, _) = GetSymbols();
        var greeter = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("IGreeter")
            && c.Kind == SymbolKind.Interface);
        greeter.Should().NotBeNull("IGreeter interface should be found");
    }

    [Fact]
    public void ExtractSymbols_FindsDiscriminatedUnionAsClass()
    {
        var (cards, _) = GetSymbols();
        var orderStatus = cards.FirstOrDefault(c => c.SymbolId.Value == "T:SampleFSharpApp.Models.OrderStatus"
            && c.Kind == SymbolKind.Class);
        orderStatus.Should().NotBeNull("OrderStatus DU should map to Class");
    }

    [Fact]
    public void ExtractSymbols_FindsMethodMembers()
    {
        var (cards, _) = GetSymbols();
        var addMethod = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("Calculator.add"));
        addMethod.Should().NotBeNull("Calculator.add function should be found");
        addMethod!.Kind.Should().Be(SymbolKind.Method);
    }

    [Fact]
    public void ExtractSymbols_XmlDocSigFormat()
    {
        var (cards, _) = GetSymbols();
        // Types should have T: prefix
        cards.Should().Contain(c => c.SymbolId.Value.StartsWith("T:"), "should have T: type IDs");
        // Methods should have M: prefix
        cards.Should().Contain(c => c.SymbolId.Value.StartsWith("M:"), "should have M: method IDs");
    }

    [Fact]
    public void ExtractSymbols_StableIdsUnique()
    {
        var (cards, stableIdMap) = GetSymbols();
        stableIdMap.Should().NotBeEmpty();

        var uniqueIds = stableIdMap.Values.Select(s => s.Value).Distinct().ToList();
        uniqueIds.Count.Should().Be(stableIdMap.Count, "all StableIds should be unique");
    }

    [Fact]
    public void ExtractSymbols_FilePathsRepoRelative()
    {
        var (cards, _) = GetSymbols();
        foreach (var card in cards)
        {
            card.FilePath.Value.Should().NotContain("\\", "paths should use forward slashes");
            card.FilePath.Value.Should().NotStartWith("/", "paths should be repo-relative");
            card.FilePath.Value.Should().EndWith(".fs", "should be F# source files");
        }
    }

    [Fact]
    public void ExtractSymbols_FindsEnumType()
    {
        var (cards, _) = GetSymbols();
        var priority = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("Priority")
            && c.Kind == SymbolKind.Enum);
        priority.Should().NotBeNull("Priority enum should be found");
    }

    [Fact]
    public void ExtractSymbols_FindsNestedModule()
    {
        var (cards, _) = GetSymbols();
        var revenue = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("Revenue"));
        revenue.Should().NotBeNull("Nested Revenue module should be found");
    }

    [Fact]
    public void ExtractSymbols_FindsMembersOfRequireQualifiedAccessModule()
    {
        var (cards, _) = GetSymbols();
        var greet = cards.FirstOrDefault(c => c.SymbolId.Value.Contains("QualifiedAccess.greet"));
        greet.Should().NotBeNull("[<RequireQualifiedAccess>] module members should be indexed");
        greet!.Kind.Should().Be(SymbolKind.Method);
    }
}
