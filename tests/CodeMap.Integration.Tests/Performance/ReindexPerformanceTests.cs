namespace CodeMap.Integration.Tests.Performance;

using System.Diagnostics;
using CodeMap.Core.Types;
using CodeMap.Roslyn;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Performance tests for IncrementalReindex after workspace caching (PHASE-04-02 T01).
///
/// After caching the MSBuildWorkspace between calls, the warm-path reindex should
/// complete under the 200ms p95 target from SYSTEM-ARCHITECTURE.MD.
///
/// Targets:
///   Cold path IncrementalReindex  — informational (not asserted)
///   Warm path IncrementalReindex  → &lt; 400ms (2× p95 headroom)
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReindexPerformanceTests : IAsyncLifetime
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static string SampleSolutionDir => Path.GetDirectoryName(SampleSolutionPath)!;

    private string _tempDir = null!;
    private IncrementalCompiler _compiler = null!;
    private CodeMap.Core.Interfaces.ISymbolStore _baseline = null!;

    private static readonly RepoId Repo = RepoId.From("perf-reindex-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));

    private static readonly FilePath ChangedFile =
        FilePath.From("SampleApp/Services/OrderService.cs");

    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-perfidx-" + Guid.NewGuid().ToString("N"));
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
    public async Task Perf_IncrementalReindex_CachedPath_Under400ms()
    {
        // Prime the cache (cold path)
        var coldSw = Stopwatch.StartNew();
        await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 0);
        coldSw.Stop();

        // Warm path — workspace already loaded, only file text update + compile
        var times = new List<long>(3);
        for (int i = 1; i <= 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await _compiler.ComputeDeltaAsync(
                SampleSolutionPath, SampleSolutionDir,
                [ChangedFile], _baseline, Repo, Sha, currentRevision: i);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        var medianWarmMs = times.OrderBy(t => t).ToList()[times.Count / 2];

        // Log for reference
        Console.WriteLine($"Cold path: {coldSw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Warm path times: {string.Join(", ", times.Select(t => $"{t}ms"))}");
        Console.WriteLine($"Warm path median: {medianWarmMs}ms");

        // Assert: 2× the p95 target (400ms) to account for CI environment variance
        medianWarmMs.Should().BeLessThan(400,
            $"cached IncrementalReindex median ({medianWarmMs}ms) should be under 400ms (2× p95 target of 200ms)");
    }

    [Fact]
    public async Task Perf_IncrementalReindex_ColdPath_Measured()
    {
        // Cold path measurement — informational only, not asserted
        // (MSBuildWorkspace startup is ~1-2s by design; caching fixes warm path)
        var sw = Stopwatch.StartNew();
        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [ChangedFile], _baseline, Repo, Sha, currentRevision: 0);
        sw.Stop();

        Console.WriteLine($"Cold path IncrementalReindex: {sw.ElapsedMilliseconds}ms");

        // The cold path should at least produce a valid delta
        delta.AddedOrUpdatedSymbols.Should().NotBeEmpty(
            "OrderService.cs symbols must be present even on cold path");
    }
}
