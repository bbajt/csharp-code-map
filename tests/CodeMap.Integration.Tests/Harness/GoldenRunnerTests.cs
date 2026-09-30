namespace CodeMap.Integration.Tests.Harness;

using CodeMap.Core.Interfaces;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Runners;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The harness golden check (<see cref="GoldenRunner"/>) run in-process against the committed samples, so CI
/// catches extraction/query drift (PHASE-21-12 T01). Until then nothing ran it, and after the PHASE-21-09 query
/// rename it silently compared 1 of 19 queries per repo.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GoldenRunnerTests : IDisposable
{
    private static readonly string CommittedGoldenDir =
        Path.Combine(FindRepoRoot(), "tests", "CodeMap.Harness", "golden");

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "codemap-golden-" + Path.GetRandomFileName());
    private readonly ServiceProvider _sp;

    public GoldenRunnerTests() => _sp = HarnessServices.Build(_dataDir, sharedCacheDir: null);

    /// <summary>Releases the store's mapped files, then removes the temporary data dir.</summary>
    public void Dispose()
    {
        (_sp.GetRequiredService<ISymbolStore>() as IDisposable)?.Dispose();
        _sp.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("SampleSolution")]
    [InlineData("SampleVbSolution")]
    [InlineData("SampleBlazorSolution")]
    public async Task GoldenCheck_CommittedSample_AllQueriesCompared_NoFailures(string repoName)
    {
        var repo = KnownRepos.ForNames([repoName], []).Repos!.Single();

        var outcome = await Runner(CommittedGoldenDir).CheckWithOutcomeAsync(repo, new ConsoleReporter(), TestContext.Current.CancellationToken);

        outcome.MissingGolden.Should().BeEmpty("every query that returns a result must have a golden file");
        outcome.Failed.Should().BeEmpty("results must match the committed golden files");
        outcome.Passed.Should().NotBeEmpty();
        outcome.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Check_MissingGoldenFile_IsFailure()
    {
        // A copy of the committed SampleSolution goldens without the symbol-search ones (old and new names).
        var goldenCopy = Path.Combine(_dataDir, "golden");
        var repoDir = Path.Combine(goldenCopy, "SampleSolution");
        Directory.CreateDirectory(repoDir);
        foreach (var file in Directory.GetFiles(Path.Combine(CommittedGoldenDir, "SampleSolution")))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("symbols_search.", StringComparison.Ordinal) || name.StartsWith("symbols.search.", StringComparison.Ordinal))
                continue;
            File.Copy(file, Path.Combine(repoDir, name));
        }

        var outcome = await Runner(goldenCopy).CheckWithOutcomeAsync(KnownRepos.SampleSolution, new ConsoleReporter(), TestContext.Current.CancellationToken);

        outcome.MissingGolden.Should().Contain(n => n.StartsWith("symbols_search:", StringComparison.Ordinal),
            "a query that returns a result but has no golden file is a failure, not a skip");
        outcome.ExitCode.Should().NotBe(0);
    }

    private GoldenRunner Runner(string goldenDir) =>
        new(_sp.GetRequiredService<IQueryEngine>(), HarnessServices.CreateIndexer(_sp), goldenDir);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CodeMap.sln not found above the test output");
    }
}
