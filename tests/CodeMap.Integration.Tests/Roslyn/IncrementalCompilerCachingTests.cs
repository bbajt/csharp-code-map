namespace CodeMap.Integration.Tests.Roslyn;

using System.Diagnostics;
using CodeMap.Core.Types;
using CodeMap.Roslyn;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Integration tests for MSBuildWorkspace caching in IncrementalCompiler (PHASE-04-02 T01).
/// Verifies that the cached solution path reuses the loaded workspace instead of reopening it.
/// Uses the real SampleSolution + MSBuildWorkspace.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalCompilerCachingTests : IAsyncLifetime
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static string SampleSolutionDir => Path.GetDirectoryName(SampleSolutionPath)!;

    private string _tempDir = null!;
    private IncrementalCompiler _compiler = null!;
    private CodeMap.Core.Interfaces.ISymbolStore _baseline = null!;

    private static readonly RepoId Repo = RepoId.From("incr-cache-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));

    private static readonly FilePath ChangedFile =
        FilePath.From("SampleApp/Services/OrderService.cs");

    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-incrcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        var roslyn = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);

        var result = await roslyn.CompileAndExtractAsync(SampleSolutionPath);
        await store.CreateBaselineAsync(Repo, Sha, result, SampleSolutionDir);

        _baseline = store;

        var differ = new SymbolDiffer(NullLogger<SymbolDiffer>.Instance);
        _compiler = new IncrementalCompiler(differ, NullLogger<IncrementalCompiler>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        _compiler.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ComputeDelta_FirstCall_ReturnsCorrectDelta()
    {
        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 0);

        delta.AddedOrUpdatedSymbols.Should().NotBeEmpty("OrderService.cs contains symbols");
        delta.NewRevision.Should().Be(1);
    }

    [Fact]
    public async Task ComputeDelta_SecondCall_SameSolution_ReturnsCorrectDelta()
    {
        // First call (cold — loads workspace)
        var delta1 = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 0);

        // Second call (warm — reuses cached solution)
        var delta2 = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 1);

        delta1.AddedOrUpdatedSymbols.Should().NotBeEmpty();
        delta2.AddedOrUpdatedSymbols.Should().NotBeEmpty();
        delta2.NewRevision.Should().Be(2);
        delta2.AddedOrUpdatedSymbols.Count.Should().Be(delta1.AddedOrUpdatedSymbols.Count,
            "same file reindexed yields same symbol count");
    }

    [Fact]
    public async Task ComputeDelta_CachedPath_FasterThanColdPath()
    {
        // Cold call (workspace creation dominates)
        var sw1 = Stopwatch.StartNew();
        await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 0);
        sw1.Stop();
        var coldMs = sw1.ElapsedMilliseconds;

        // Warm call (workspace already loaded)
        var sw2 = Stopwatch.StartNew();
        await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 1);
        sw2.Stop();
        var warmMs = sw2.ElapsedMilliseconds;

        // Cached path should be meaningfully faster
        warmMs.Should().BeLessThan(coldMs,
            $"cached path ({warmMs}ms) should be faster than cold path ({coldMs}ms)");
    }

    [Fact]
    public async Task ComputeDelta_EmptyChangedFiles_ReturnsDeltaWithoutOpeningWorkspace()
    {
        // Empty file list returns early — no workspace opened, cache untouched
        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [], _baseline, Repo, Sha, currentRevision: 5);

        delta.AddedOrUpdatedSymbols.Should().BeEmpty();
        delta.NewRevision.Should().Be(6);
    }

    [Fact]
    public async Task ComputeDelta_MultipleWarmCalls_AllReturnCorrectDeltas()
    {
        // Prime the cache
        await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 0);

        // Three subsequent warm calls
        for (int rev = 1; rev <= 3; rev++)
        {
            var delta = await _compiler.ComputeDeltaAsync(
                SampleSolutionPath, SampleSolutionDir,
                [ChangedFile], _baseline, Repo, Sha, currentRevision: rev);

            delta.AddedOrUpdatedSymbols.Should().NotBeEmpty(
                $"warm call {rev} should still return symbols from OrderService.cs");
            delta.NewRevision.Should().Be(rev + 1);
        }
    }

    [Fact]
    public void Dispose_AfterCalls_NoException()
    {
        // Should not throw even if called before any ComputeDeltaAsync
        var act = () => _compiler.Dispose();
        act.Should().NotThrow();
    }
}
