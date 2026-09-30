namespace CodeMap.Integration.Tests.Roslyn;

using CodeMap.Core.Types;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="IncrementalCompiler.EvictIfIdle"/> on the real SampleSolution and v2 store (PHASE-21-12 T02):
/// an idle cached solution is dropped, and the next refresh reopens it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalCompilerEvictionTests : IAsyncLifetime
{
    private static readonly string SampleSolutionPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static readonly string SampleSolutionDir = Path.GetDirectoryName(SampleSolutionPath)!;
    private static readonly RepoId Repo = RepoId.From("incr-evict-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));
    private static readonly FilePath ChangedFile = FilePath.From("SampleApp/Services/OrderService.cs");

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "codemap-evict-" + Path.GetRandomFileName());
    private CustomSymbolStore _store = null!;
    private IncrementalCompiler _compiler = null!;

    /// <summary>Builds the SampleSolution baseline into a temp v2 store.</summary>
    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();
        _store = new CustomSymbolStore(_storeDir);
        var result = await new RoslynCompiler(NullLogger<RoslynCompiler>.Instance)
            .CompileAndExtractAsync(SampleSolutionPath, TestContext.Current.CancellationToken);
        await _store.CreateBaselineAsync(Repo, Sha, result, SampleSolutionDir, TestContext.Current.CancellationToken);
        _compiler = new IncrementalCompiler(new SymbolDiffer(NullLogger<SymbolDiffer>.Instance),
            NullLogger<IncrementalCompiler>.Instance);
    }

    /// <summary>Disposes the compiler and store, then removes the temp store.</summary>
    public ValueTask DisposeAsync()
    {
        _compiler.Dispose();
        _store.Dispose();
        try { Directory.Delete(_storeDir, recursive: true); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EvictIfIdle_DropsCachedSolution_NextRefreshIsColdOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await _compiler.ComputeDeltaAsync(SampleSolutionPath, SampleSolutionDir, [ChangedFile], _store, Repo, Sha, 0, ct);
        _compiler.HasCachedSolution.Should().BeTrue();

        _compiler.EvictIfIdle(TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow).Should().BeFalse("the solution was just used");
        _compiler.EvictIfIdle(TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow.AddMinutes(11)).Should().BeTrue();
        _compiler.HasCachedSolution.Should().BeFalse();

        var delta = await _compiler.ComputeDeltaAsync(SampleSolutionPath, SampleSolutionDir, [ChangedFile], _store, Repo, Sha, 1, ct);

        delta.AddedOrUpdatedSymbols.Should().NotBeEmpty("the cold reopen works after an eviction");
        _compiler.HasCachedSolution.Should().BeTrue();
    }

    [Fact]
    public void EvictIfIdle_NothingCached_ReturnsFalse() =>
        _compiler.EvictIfIdle(TimeSpan.Zero, DateTimeOffset.UtcNow.AddDays(1)).Should().BeFalse();
}
