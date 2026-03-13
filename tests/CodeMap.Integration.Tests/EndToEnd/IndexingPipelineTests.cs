namespace CodeMap.Integration.Tests.EndToEnd;

using CodeMap.Core.Enums;
using CodeMap.Roslyn;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests focused on the indexing pipeline (IRoslynCompiler → ISymbolStore).
/// Verifies that SampleSolution is indexed with correct symbol counts and kinds.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexingPipelineTests
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static RoslynCompiler CreateCompiler() =>
        new(NullLogger<RoslynCompiler>.Instance);

    [Fact]
    public async Task Index_SampleSolution_ExtractsClasses()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        result.Symbols.Should().Contain(s => s.Kind == Core.Enums.SymbolKind.Class);
    }

    [Fact]
    public async Task Index_SampleSolution_ExtractsInterfaces()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        result.Symbols.Should().Contain(s => s.Kind == Core.Enums.SymbolKind.Interface);
    }

    [Fact]
    public async Task Index_SampleSolution_ExtractsMethods()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        result.Symbols.Should().Contain(s => s.Kind == Core.Enums.SymbolKind.Method);
    }

    [Fact]
    public async Task Index_SampleSolution_ExtractsProperties()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        result.Symbols.Should().Contain(s => s.Kind == Core.Enums.SymbolKind.Property);
    }

    [Fact]
    public async Task Index_SampleSolution_StatsNonZero()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        result.Stats.SymbolCount.Should().BeGreaterThan(0);
        result.Stats.FileCount.Should().BeGreaterThan(0);
        result.Stats.ElapsedSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Index_SampleSolution_ZeroCompilationErrors()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        result.Stats.Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public async Task Index_SampleSolution_ExtractsReferences()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // Before the fix, result.References had 0 rows due to file path mismatch.
        result.References.Should().NotBeEmpty();
        result.Stats.ReferenceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Index_SampleSolution_RefFilePathsAreRepoRelative()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // Ref file paths must be repo-relative (no leading slash, no absolute path).
        result.References.Should().AllSatisfy(r =>
        {
            r.FilePath.Value.Should().NotBeEmpty();
            r.FilePath.Value.Should().NotStartWith("/");
            r.FilePath.Value.Should().NotContain(":\\"); // no Windows absolute path
        });
    }

    [Fact]
    public async Task Index_SampleSolution_RefFilePathsMatchExtractedFiles()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // Every ref's file path must be present in the extracted files list.
        // This verifies that refs.find queries can join refs → files table.
        var filePathSet = result.Files.Select(f => f.Path.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanedRefs = result.References
            .Where(r => !filePathSet.Contains(r.FilePath.Value))
            .ToList();

        orphanedRefs.Should().BeEmpty(
            "all ref file paths must match extracted file paths so InsertRefsAsync doesn't skip them");
    }

    [Fact]
    public async Task Index_SampleSolution_RefsPersistedInBaseline()
    {
        MsBuildInitializer.EnsureRegistered();

        var tempDir = Path.Combine(Path.GetTempPath(), "codemap-refs-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var factory = new BaselineDbFactory(tempDir, NullLogger<BaselineDbFactory>.Instance);
            var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
            var compiler = CreateCompiler();
            var repoId = Core.Types.RepoId.From("refs-pipeline-test");
            var commitSha = Core.Types.CommitSha.From(new string('b', 40));

            var compiled = await compiler.CompileAndExtractAsync(SampleSolutionPath);
            await store.CreateBaselineAsync(repoId, commitSha, compiled);

            // Verify refs were persisted by querying the DB directly.
            // Before the fix, InsertRefsAsync skipped every ref because file paths
            // were absolute (e.g. C:\...\file.cs) but fileIdByPath keys were repo-relative.
            var dbPath = factory.GetDbPath(repoId, commitSha);
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM refs";
            var refCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            refCount.Should().BeGreaterThan(0,
                "SampleSolution has cross-class calls; refs must be persisted after the file-path fix");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Index_SampleSolution_StoresAndRetrievesSymbols()
    {
        MsBuildInitializer.EnsureRegistered();

        var tempDir = Path.Combine(Path.GetTempPath(), "codemap-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var factory = new BaselineDbFactory(tempDir, NullLogger<BaselineDbFactory>.Instance);
            var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
            var compiler = CreateCompiler();
            var repoId = Core.Types.RepoId.From("pipeline-test-repo");
            var commitSha = Core.Types.CommitSha.From(new string('a', 40));

            var compiled = await compiler.CompileAndExtractAsync(SampleSolutionPath);
            await store.CreateBaselineAsync(repoId, commitSha, compiled);

            var exists = await store.BaselineExistsAsync(repoId, commitSha);
            exists.Should().BeTrue();

            var hits = await store.SearchSymbolsAsync(repoId, commitSha, "OrderService", null, 10);
            hits.Should().NotBeEmpty();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
