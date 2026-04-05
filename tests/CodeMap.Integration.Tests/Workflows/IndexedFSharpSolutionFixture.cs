namespace CodeMap.Integration.Tests.Workflows;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Shared xUnit class fixture that indexes SampleFSharpSolution once using RoslynCompiler
/// (which dispatches F# projects to FSharp.Compiler.Service).
/// Exposes QueryEngine + BaselineStore for F# regression integration tests.
/// </summary>
public sealed class IndexedFSharpSolutionFixture : IAsyncLifetime
{
    private static string SampleFSharpSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleFSharpSolution", "SampleFSharpSolution.sln"));

    public static string SampleFSharpSolutionDir => Path.GetDirectoryName(SampleFSharpSolutionPath)!;

    private string _tempDir = null!;

    // ── Exposed infrastructure ────────────────────────────────────────────────

    public RepoId RepoId { get; } = RepoId.From("fsharp-workflow-fixture-repo");
    public CommitSha Sha { get; } = CommitSha.From(new string('a', 40));

    public ISymbolStore BaselineStore { get; private set; } = null!;
    public QueryEngine QueryEngine { get; private set; } = null!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-fs-fixture-" + Guid.NewGuid().ToString("N"));
        var baselineDir = Path.Combine(_tempDir, "baselines");
        Directory.CreateDirectory(baselineDir);

        BaselineStore = new CustomSymbolStore(baselineDir);
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var cache = new InMemoryCacheService();
        var tracker = new TokenSavingsTracker();

        var compiled = await compiler.CompileAndExtractAsync(SampleFSharpSolutionPath);
        await BaselineStore.CreateBaselineAsync(RepoId, Sha, compiled, SampleFSharpSolutionDir);

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

    public RoutingContext CommittedRouting() =>
        new(repoId: RepoId, baselineCommitSha: Sha);
}
