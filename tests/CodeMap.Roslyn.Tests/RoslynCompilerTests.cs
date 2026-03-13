namespace CodeMap.Roslyn.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Integration")]
public class RoslynCompilerTests
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static RoslynCompiler CreateCompiler() =>
        new(NullLogger<RoslynCompiler>.Instance);

    [Fact]
    public async Task CompileAndExtract_ValidSolution_ReturnsSymbols()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        result.Symbols.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CompileAndExtract_ValidSolution_ReturnsFiles()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        result.Files.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CompileAndExtract_ValidSolution_StatsMatchCounts()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        result.Stats.SymbolCount.Should().Be(result.Symbols.Count);
        result.Stats.ReferenceCount.Should().Be(result.References.Count);
        result.Stats.FileCount.Should().Be(result.Files.Count);
    }

    [Fact]
    public async Task CompileAndExtract_ValidSolution_NoLowConfidenceSymbols()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        result.Symbols.Should().NotContain(s => s.Confidence == Confidence.Low);
    }

    [Fact]
    public async Task CompileAndExtract_ValidSolution_ExtractsMultipleSymbolKinds()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);
        var kinds = result.Symbols.Select(s => s.Kind).Distinct().ToList();
        kinds.Should().Contain(SymbolKind.Class);
        kinds.Should().Contain(SymbolKind.Interface);
        kinds.Should().Contain(SymbolKind.Method);
    }

    [Fact]
    public async Task CompileAndExtract_InvalidSolutionPath_ThrowsFileNotFoundException()
    {
        var compiler = CreateCompiler();
        var act = async () => await compiler.CompileAndExtractAsync("/nonexistent/path/solution.sln");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task CompileAndExtract_CancellationRequested_ThrowsOperationCanceled()
    {
        var compiler = CreateCompiler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await compiler.CompileAndExtractAsync(SampleSolutionPath, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IncrementalExtract_NoChangedFiles_ReturnsEmpty()
    {
        var compiler = CreateCompiler();
        var result = await compiler.IncrementalExtractAsync(SampleSolutionPath, []);
        result.Symbols.Should().BeEmpty();
        result.References.Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementalExtract_ChangedFile_ReturnsOnlyAffectedSymbols()
    {
        var compiler = CreateCompiler();
        var full = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // Pick one file from the results
        var someFile = full.Symbols.FirstOrDefault()?.FilePath;
        if (someFile is null) return;  // No symbols found

        var result = await compiler.IncrementalExtractAsync(SampleSolutionPath, [someFile.Value]);

        result.Symbols.Should().NotBeEmpty();
        result.Symbols.Should().AllSatisfy(s => s.FilePath.Value.Should().Be(someFile.Value.Value));
    }
}
