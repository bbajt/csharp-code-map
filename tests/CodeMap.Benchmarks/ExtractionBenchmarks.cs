namespace CodeMap.Benchmarks;

using BenchmarkDotNet.Attributes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// BenchmarkDotNet benchmarks for extraction operations (PHASE-04-01 T02).
///
/// Measures:
///   FullIndex         — Roslyn compilation + extraction + SQLite write (~10-30s)
///   IncrementalReindex — recompile one changed file, apply delta (~200ms)
///   CachePull         — pull baseline from shared cache dir (~2ms)
///
/// Uses ExtractionBenchmarkConfig (1 warmup, 3 iterations) to keep
/// total wall time reasonable for the slow FullIndex benchmark.
///
/// Run: dotnet run --project tests/CodeMap.Benchmarks -c Release -- --filter "ExtractionBenchmarks*"
/// </summary>
[Config(typeof(ExtractionBenchmarkConfig))]
[MemoryDiagnoser]
public class ExtractionBenchmarks
{
    private static string SampleSolutionPath => FindSolutionFile();
    private static string SampleSolutionDir => Path.GetDirectoryName(SampleSolutionPath)!;

    private static string FindSolutionFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "SampleSolution", "SampleSolution.sln");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));
    }

    private string _tempDir = null!;
    private string _cacheDir = null!;
    private RoslynCompiler _compiler = null!;
    private RepoId _repoId;
    private CommitSha _commitSha;

    // FullIndex iteration state
    private string _iterDir = null!;
    private ISymbolStore _iterStore = null!;

    // IncrementalReindex state
    private WorkspaceManager _wsMgr = null!;
    private WorkspaceId _wsId;
    private FilePath _changedFile;

    // CachePull iteration state
    private string _pullDir = null!;

    // ── Null-object implementations ──────────────────────────────────────────

    private sealed class NoopGitService : IGitService
    {
        private readonly CommitSha _sha;
        public NoopGitService(CommitSha sha) => _sha = sha;

        public Task<RepoId> GetRepoIdentityAsync(string r, CancellationToken ct) => Task.FromResult(RepoId.From("xbench-repo"));
        public Task<CommitSha> GetCurrentCommitAsync(string r, CancellationToken ct) => Task.FromResult(_sha);
        public Task<string> GetCurrentBranchAsync(string r, CancellationToken ct) => Task.FromResult("main");
        public Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(string r, CommitSha b, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FileChange>>([]);
        public Task<bool> IsCleanAsync(string r, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NullResolutionWorker : IResolutionWorker
    {
        public Task<int> ResolveEdgesAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ResolveOverlayEdgesAsync(
            RepoId repoId, CommitSha commitSha, WorkspaceId workspaceId,
            IReadOnlyList<FilePath> recompiledFiles,
            IOverlayStore overlayStore, ISymbolStore baselineStore, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    // ── Setup / Cleanup ──────────────────────────────────────────────────────

    [GlobalSetup]
    public async Task Setup()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-xbench-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_cacheDir);

        _repoId = RepoId.From("xbench-repo");
        _commitSha = CommitSha.From(new string('d', 40));
        _compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);

        // Build a baseline to power IncrementalReindex and CachePull setup
        var baselineDir = Path.Combine(_tempDir, "baseline");
        Directory.CreateDirectory(baselineDir);
        var baseStore = new CustomSymbolStore(baselineDir);
        var compiled = await _compiler.CompileAndExtractAsync(SampleSolutionPath).ConfigureAwait(false);
        await baseStore.CreateBaselineAsync(_repoId, _commitSha, compiled, SampleSolutionDir).ConfigureAwait(false);

        // Pre-populate shared cache for CachePull benchmark
        var cacheMgr = new EngineBaselineCacheManager(baselineDir, _cacheDir);
        await cacheMgr.PushAsync(_repoId, _commitSha).ConfigureAwait(false);

        // Set up WorkspaceManager for IncrementalReindex benchmark
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(overlayDir);
        var overlayStore = new CustomEngineOverlayStore(baseStore, baselineDir);
        var differ = new SymbolDiffer(NullLogger<SymbolDiffer>.Instance);
        var incCompiler = new IncrementalCompiler(differ, NullLogger<IncrementalCompiler>.Instance);
        var cache = new InMemoryCacheService();

        _wsMgr = new WorkspaceManager(
            overlayStore, incCompiler, baseStore,
            new NoopGitService(_commitSha), cache,
            new NullResolutionWorker(),
            NullLogger<WorkspaceManager>.Instance);

        _wsId = WorkspaceId.From("xbench-ws");
        await _wsMgr.CreateWorkspaceAsync(
            _repoId, _wsId, _commitSha, SampleSolutionPath, SampleSolutionDir).ConfigureAwait(false);

        // Pick a file to "change" — OrderService.cs is a mid-size file
        _changedFile = FilePath.From("SampleApp/Services/OrderService.cs");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── FullIndex: per-iteration fresh store ─────────────────────────────────

    [IterationSetup(Target = nameof(FullIndex))]
    public void SetupFullIndex()
    {
        _iterDir = Path.Combine(_tempDir, "iter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_iterDir);
        _iterStore = new CustomSymbolStore(_iterDir);
    }

    [IterationCleanup(Target = nameof(FullIndex))]
    public void CleanupFullIndex()
    {
        try { Directory.Delete(_iterDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Full baseline index: Roslyn compilation + extraction + SQLite write.
    /// Target: p95 &lt; 30s (100-file solution).
    /// SampleSolution is smaller — expect 5-20s.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1)]
    public async Task FullIndex()
    {
        var result = await _compiler.CompileAndExtractAsync(SampleSolutionPath).ConfigureAwait(false);
        await _iterStore.CreateBaselineAsync(_repoId, _commitSha, result, SampleSolutionDir).ConfigureAwait(false);
    }

    // ── IncrementalReindex ────────────────────────────────────────────────────

    /// <summary>
    /// Incremental re-index of one changed file via WorkspaceManager.
    /// Target: p95 &lt; 200ms.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1)]
    public async Task IncrementalReindex()
    {
        // Pass the file explicitly — skips git diff, directly recompiles changed file
        await _wsMgr.RefreshOverlayAsync(_repoId, _wsId, [_changedFile]).ConfigureAwait(false);
    }

    // ── CachePull: per-iteration fresh local dir ─────────────────────────────

    [IterationSetup(Target = nameof(CachePull))]
    public void SetupCachePull()
    {
        _pullDir = Path.Combine(_tempDir, "pull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pullDir);
    }

    [IterationCleanup(Target = nameof(CachePull))]
    public void CleanupCachePull()
    {
        try { Directory.Delete(_pullDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Pull baseline from shared cache (file copy + validation).
    /// Target: &lt; 500ms.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1)]
    public async Task CachePull()
    {
        var pullMgr = new EngineBaselineCacheManager(_pullDir, _cacheDir);
        await pullMgr.PullAsync(_repoId, _commitSha).ConfigureAwait(false);
    }
}
