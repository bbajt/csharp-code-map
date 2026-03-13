namespace CodeMap.Roslyn.Tests;

using System.Diagnostics;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests that RoslynCompiler and IncrementalCompiler work with .slnx solution files.
/// Roslyn 5.x added .slnx support via Microsoft.VisualStudio.SolutionPersistence
/// (PR #77326). These tests guard against regression.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlnxSupportTests
{
    private static string FindSampleSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "SampleSolution");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find testdata/SampleSolution directory.");
    }

    private static readonly RepoId Repo = RepoId.From("slnx-test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));

    [Fact]
    public async Task RoslynCompiler_SlnxSolution_ProducesFullSemanticLevel()
    {
        // Arrange
        var solutionDir = FindSampleSolutionDir();
        var slnxPath = Path.Combine(solutionDir, "SampleSolution.slnx");
        File.Exists(slnxPath).Should().BeTrue($".slnx file must exist at {slnxPath}");

        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);

        // Act — CompileAndExtractAsync returns CompilationResult (throws on failure)
        var result = await compiler.CompileAndExtractAsync(slnxPath, CancellationToken.None);

        // Assert
        result.Stats.SemanticLevel.Should().Be(SemanticLevel.Full,
            ".slnx compilation should produce full semantic index");
        result.Symbols.Should().NotBeEmpty("symbols should be extracted from .slnx");
    }

    [Fact]
    public async Task RoslynCompiler_SlnxSolution_SameSymbolCountAsSln()
    {
        // Arrange
        var solutionDir = FindSampleSolutionDir();
        var slnPath  = Path.Combine(solutionDir, "SampleSolution.sln");
        var slnxPath = Path.Combine(solutionDir, "SampleSolution.slnx");

        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);

        // Act
        var slnResult  = await compiler.CompileAndExtractAsync(slnPath,  CancellationToken.None);
        var slnxResult = await compiler.CompileAndExtractAsync(slnxPath, CancellationToken.None);

        // Assert
        slnxResult.Symbols.Count.Should().Be(slnResult.Symbols.Count,
            ".slnx and .sln should produce identical symbol counts");
    }

    [Fact]
    public async Task IncrementalCompiler_SlnxSolution_WarmPathFasterThanColdPath()
    {
        // Arrange
        var solutionDir = FindSampleSolutionDir();
        var slnxPath = Path.Combine(solutionDir, "SampleSolution.slnx");

        var differ = new SymbolDiffer(NullLogger<SymbolDiffer>.Instance);
        using var compiler = new IncrementalCompiler(differ, NullLogger<IncrementalCompiler>.Instance);

        // Use a mock baseline store — SymbolDiffer only calls GetSymbolsByFileAsync
        var mockStore = Substitute.For<ISymbolStore>();
        mockStore.GetSymbolsByFileAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<FilePath>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SymbolCard>>([]));

        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        // Act — cold path (workspace must be loaded from disk)
        var sw1 = Stopwatch.StartNew();
        var cold = await compiler.ComputeDeltaAsync(
            slnxPath, solutionDir,
            [changedFile], mockStore, Repo, Sha, currentRevision: 0);
        sw1.Stop();

        // Act — warm path (workspace is cached from first call)
        var sw2 = Stopwatch.StartNew();
        var warm = await compiler.ComputeDeltaAsync(
            slnxPath, solutionDir,
            [changedFile], mockStore, Repo, Sha, currentRevision: 1);
        sw2.Stop();

        // Assert
        cold.AddedOrUpdatedSymbols.Should().NotBeEmpty("cold .slnx compilation should find symbols in OrderService.cs");
        warm.AddedOrUpdatedSymbols.Should().NotBeEmpty("warm .slnx compilation should find symbols in OrderService.cs");
        sw2.ElapsedMilliseconds.Should().BeLessThan(sw1.ElapsedMilliseconds,
            $"warm path ({sw2.ElapsedMilliseconds}ms) should be faster than cold path ({sw1.ElapsedMilliseconds}ms) due to cached MSBuildWorkspace");
    }
}
