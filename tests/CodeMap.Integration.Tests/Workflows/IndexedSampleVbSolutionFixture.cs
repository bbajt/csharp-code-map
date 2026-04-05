namespace CodeMap.Integration.Tests.Workflows;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Shared xUnit class fixture that indexes SampleVbSolution once using real Roslyn.
/// Exposes QueryEngine + BaselineStore for VB.NET regression integration tests.
/// Shared across test classes via IClassFixture — indexing only happens once per run.
/// </summary>
public sealed class IndexedSampleVbSolutionFixture : IAsyncLifetime
{
    private static string SampleVbSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleVbSolution", "SampleVbSolution.sln"));

    public static string SampleVbSolutionDir => Path.GetDirectoryName(SampleVbSolutionPath)!;

    private string _tempDir = null!;

    // ── Exposed infrastructure ────────────────────────────────────────────────

    public RepoId RepoId { get; } = RepoId.From("vb-workflow-fixture-repo");
    public CommitSha Sha { get; } = CommitSha.From(new string('f', 40));

    public ISymbolStore BaselineStore { get; private set; } = null!;
    public QueryEngine QueryEngine { get; private set; } = null!;
    public string OverlayDir { get; private set; } = null!;
    public string BaselineDir { get; private set; } = null!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-vb-fixture-" + Guid.NewGuid().ToString("N"));
        OverlayDir = Path.Combine(_tempDir, "overlays");
        BaselineDir = Path.Combine(_tempDir, "baselines");
        Directory.CreateDirectory(BaselineDir);
        Directory.CreateDirectory(OverlayDir);

        BaselineStore = new CustomSymbolStore(BaselineDir);
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        var compiled = await compiler.CompileAndExtractAsync(SampleVbSolutionPath);
        await BaselineStore.CreateBaselineAsync(RepoId, Sha, compiled, SampleVbSolutionDir);

        QueryEngine = new QueryEngine(
            BaselineStore, cache, tracker,
            new ExcerptReader(BaselineStore), new GraphTraverser(),
            new FeatureTracer(BaselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public RoutingContext CommittedRouting() =>
        new(repoId: RepoId, baselineCommitSha: Sha);
}
