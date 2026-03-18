namespace CodeMap.Integration.Tests.Roslyn;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Roslyn;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Integration tests for IncrementalCompiler against the real SampleSolution.
/// Uses MSBuildWorkspace + BaselineStore. Run one at a time (sequential fixture).
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalCompilerTests : IAsyncLifetime
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static string SampleSolutionDir => Path.GetDirectoryName(SampleSolutionPath)!;

    private string _tempDir = null!;
    private ISymbolStore _baseline = null!;
    private IncrementalCompiler _compiler = null!;

    private static readonly RepoId Repo = RepoId.From("incr-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('f', 40));

    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();

        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-incr-" + Guid.NewGuid().ToString("N"));
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
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Incremental_NoProjectAffected_ReturnsEmptyDelta()
    {
        var nonExistent = FilePath.From("src/DoesNotExist.cs");

        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [nonExistent], _baseline, Repo, Sha, currentRevision: 0);

        delta.AddedOrUpdatedSymbols.Should().BeEmpty();
        delta.ReindexedFiles.Should().BeEmpty();
        delta.NewRevision.Should().Be(1);
    }

    [Fact]
    public async Task Incremental_ChangedFileSymbols_AppearInDelta()
    {
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [changedFile], _baseline, Repo, Sha, currentRevision: 0);

        delta.AddedOrUpdatedSymbols.Should().NotBeEmpty("OrderService.cs contains class/method symbols");
        delta.ReindexedFiles.Should().NotBeEmpty();
        delta.NewRevision.Should().Be(1);
    }

    [Fact]
    public async Task Incremental_UnchangedFileSymbols_NotInDelta()
    {
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [changedFile], _baseline, Repo, Sha, currentRevision: 0);

        var repositoryInDelta = delta.AddedOrUpdatedSymbols
            .Any(s => s.FilePath.Value.Contains("Repository"));
        repositoryInDelta.Should().BeFalse("Repository.cs was not listed as changed");
    }

    [Fact]
    public async Task Incremental_ChangedFile_ReindexedFileMetadataPresent()
    {
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [changedFile], _baseline, Repo, Sha, currentRevision: 0);

        delta.ReindexedFiles.Should().Contain(f => f.Path.Value.Contains("OrderService.cs"));
    }

    [Fact]
    public async Task Incremental_ChangedFile_DeletedReferenceFilesMatchChangedFiles()
    {
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [changedFile], _baseline, Repo, Sha, currentRevision: 0);

        delta.DeletedReferenceFiles.Should().Contain(changedFile);
    }

    [Fact]
    public async Task Incremental_SameContentAsBaseline_NoDeletedSymbols()
    {
        // When the same solution is re-indexed (no real changes), no symbols are deleted
        var changedFile = FilePath.From("SampleApp/Services/OrderService.cs");

        var delta = await _compiler.ComputeDeltaAsync(
            SampleSolutionPath, SampleSolutionDir,
            [changedFile], _baseline, Repo, Sha, currentRevision: 2);

        delta.DeletedSymbolIds.Should().BeEmpty(
            "symbols that exist in both baseline and new extraction are not deleted");
        delta.NewRevision.Should().Be(3);
    }
}
