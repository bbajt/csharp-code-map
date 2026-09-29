namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// F12 (PHASE-21-07 T02): an unrestored checkout (fresh clone / git worktree: <c>obj/</c> is
/// gitignored and MSBuildWorkspace doesn't restore) compiles with unresolved references. That must
/// never be silent. The test works on a copy of <c>testdata/SampleSolution</c> without <c>bin/</c> and
/// <c>obj/</c>, so the real checkout is never restored or modified.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnrestoredCheckoutTests : IDisposable
{
    private static string SampleDir => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata", "SampleSolution"));

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "codemap-f12-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Removes the temp copy (best-effort).</summary>
    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Unrestored_PackagesMissing_ReportedAndPartial()
    {
        var copy = Path.Combine(_tempDir, "src");
        CopyWithoutBuildOutput(SampleDir, copy);
        MsBuildInitializer.EnsureRegistered();

        var result = await new RoslynCompiler(NullLogger<RoslynCompiler>.Instance)
            .CompileAndExtractAsync(Path.Combine(copy, "SampleSolution.sln"), TestContext.Current.CancellationToken);

        var diags = result.Stats.ProjectDiagnostics!;
        var withErrors = diags.Where(d => d.Errors is { Count: > 0 }).ToList();
        if (withErrors.Count == 0)
            return; // everything resolved without restore on this machine: nothing was lost

        // Invariant: unresolved references must be reported and must lower the level.
        withErrors.Should().OnlyContain(d => d.MissingRestoreOutput != null,
            "a project compiled without restore output must say so");
        result.Stats.SemanticLevel.Should().Be(SemanticLevel.Partial,
            $"{withErrors.Count} project(s) compiled with unresolved references ({string.Join(", ", withErrors.Select(d => $"{d.ProjectName}: {d.Errors![0]}"))})");
    }

    [Fact]
    public async Task Unrestored_StoredBaseline_QueryReportsPartial()
    {
        var copy = Path.Combine(_tempDir, "src");
        CopyWithoutBuildOutput(SampleDir, copy);
        MsBuildInitializer.EnsureRegistered();
        var ct = TestContext.Current.CancellationToken;

        var compiled = await new RoslynCompiler(NullLogger<RoslynCompiler>.Instance)
            .CompileAndExtractAsync(Path.Combine(copy, "SampleSolution.sln"), ct);
        if (compiled.Stats.ProjectDiagnostics!.All(d => d.Errors is not { Count: > 0 }))
            return; // see the test above

        using var store = new CustomSymbolStore(Path.Combine(_tempDir, "store"));
        var repo = RepoId.From("f12-repo");
        var sha = CommitSha.From(new string('f', 40));
        await store.CreateBaselineAsync(repo, sha, compiled, copy, ct);
        var engine = new QueryEngine(
            store, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(store), new GraphTraverser(),
            new FeatureTracer(store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var search = await engine.SearchSymbolsAsync(
            new Core.Models.RoutingContext(repoId: repo, baselineCommitSha: sha), "OrderService", null, null, ct);

        search.IsSuccess.Should().BeTrue();
        search.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.Partial);
    }

    [Fact]
    public async Task Restored_Checkout_NoMissingRestoreOutput()
    {
        // The integration suite assumes the testdata fixtures are restored (`dotnet restore
        // testdata/SampleSolution/SampleSolution.sln` once per checkout; the Linux rig image does it).
        // Compiling it here is read-only.
        MsBuildInitializer.EnsureRegistered();
        var result = await new RoslynCompiler(NullLogger<RoslynCompiler>.Instance)
            .CompileAndExtractAsync(Path.Combine(SampleDir, "SampleSolution.sln"), TestContext.Current.CancellationToken);

        result.Stats.ProjectDiagnostics!.Should().OnlyContain(d => d.MissingRestoreOutput == null);
        result.Stats.SemanticLevel.Should().Be(SemanticLevel.Full);
    }

    private static void CopyWithoutBuildOutput(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(d);
            if (name is "bin" or "obj") continue;
            CopyWithoutBuildOutput(d, Path.Combine(dst, name));
        }
    }
}
