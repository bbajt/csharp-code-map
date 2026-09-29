namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// F13 (PHASE-21-07 T01): the semantic level a stored baseline reports must use the same rule as
/// the build (<c>RoslynCompiler.ComputeSemanticLevel</c>) — before the fix the store recomputed it from
/// <c>Compiled</c> alone, so a project whose source generators failed to load (F10) read as Full.
/// </summary>
public sealed class CustomSymbolStoreSemanticLevelTests : IDisposable
{
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
    private static readonly RepoId Repo = RepoId.From("f13-repo");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-f13-{Guid.NewGuid():N}");
    private readonly CustomSymbolStore _store;

    /// <summary>Creates the store over a fresh temp dir.</summary>
    public CustomSymbolStoreSemanticLevelTests() => _store = new CustomSymbolStore(_tempDir);

    /// <summary>Releases the store and removes the temp dir.</summary>
    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task GetSemanticLevelAsync_ProjectWithGeneratorLoadFailures_ReturnsPartial()
    {
        await _store.CreateBaselineAsync(Repo, Sha, Data(
        [
            new ProjectDiagnostic("Blazor", true, 1, 0,
                GeneratorLoadFailures: ["Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)"]),
            new ProjectDiagnostic("Core", true, 1, 0),
        ]), @"C:\repo", TestContext.Current.CancellationToken);

        var level = await _store.GetSemanticLevelAsync(Repo, Sha, TestContext.Current.CancellationToken);

        level.Should().Be(SemanticLevel.Partial, "a stored baseline must report the level the build computed");
    }

    [Fact]
    public async Task GetSemanticLevelAsync_AllProjectsComplete_ReturnsFull()
    {
        await _store.CreateBaselineAsync(Repo, Sha, Data(
        [
            new ProjectDiagnostic("Web", true, 1, 0),
            new ProjectDiagnostic("Core", true, 1, 0),
        ]), @"C:\repo", TestContext.Current.CancellationToken);

        (await _store.GetSemanticLevelAsync(Repo, Sha, TestContext.Current.CancellationToken))
            .Should().Be(SemanticLevel.Full);
    }

    private static CompilationResult Data(IReadOnlyList<ProjectDiagnostic> diagnostics)
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/App/Foo.cs"), "aa" + new string('0', 62), "Core", "public class Foo { }"),
        };
        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Foo"), "global::MyApp.Foo", SymbolKind.Class,
                "public class Foo", "MyApp", FilePath.From("src/App/Foo.cs"), 1, 1, "public", Confidence.High),
        };
        return new CompilationResult(symbols, [], files,
            new IndexStats(symbols.Count, 0, files.Count, 0.0, Confidence.Medium,
                SemanticLevel.Partial, diagnostics));
    }
}
