namespace CodeMap.Integration.Tests.Storage;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// F13 (PHASE-21-07 T01): a degradation the build detected must reach every query's
/// <c>meta.semantic_level</c>, not only the <c>index.ensure_baseline</c> response. Before the fix the
/// manifest dropped <see cref="ProjectDiagnostic.GeneratorLoadFailures"/> and the store recomputed the
/// level from <c>Compiled</c> alone, so queries against an F10-degraded baseline said Full.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SemanticLevelPersistenceTests : IDisposable
{
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));
    private static readonly RepoId Repo = RepoId.From("f13-integration-repo");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "codemap-f13-int-" + Guid.NewGuid().ToString("N"));
    private readonly CustomSymbolStore _store;

    /// <summary>Creates a real store over a fresh temp dir.</summary>
    public SemanticLevelPersistenceTests() => _store = new CustomSymbolStore(_tempDir);

    /// <summary>Releases the store and removes the temp dir.</summary>
    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Query_AfterBaselineWithDegradedProject_ReportsPartial()
    {
        var ct = TestContext.Current.CancellationToken;
        var diagnostics = new List<ProjectDiagnostic>
        {
            new("SampleBlazor", true, 1, 0,
                GeneratorLoadFailures: ["Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)"]),
            new("SampleCore", true, 1, 0),
        };
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/Core/Widget.cs"), "aa" + new string('0', 62), "SampleCore", "public class Widget { }"),
        };
        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:SampleCore.Widget"), "global::SampleCore.Widget", SymbolKind.Class,
                "public class Widget", "SampleCore", FilePath.From("src/Core/Widget.cs"), 1, 1, "public", Confidence.High),
        };
        // What RoslynCompiler returns for this case since ADR-046: level Partial, confidence Medium.
        var compiled = new CompilationResult(symbols, [], files,
            new IndexStats(symbols.Count, 0, files.Count, 0.0, Confidence.Medium, SemanticLevel.Partial, diagnostics));
        await _store.CreateBaselineAsync(Repo, Sha, compiled, _tempDir, ct);

        var engine = new QueryEngine(
            _store, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(_store), new GraphTraverser(),
            new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var result = await engine.SearchSymbolsAsync(
            new RoutingContext(repoId: Repo, baselineCommitSha: Sha), "Widget", null, null, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().NotBeEmpty();
        result.Value.Meta.SemanticLevel.Should().Be(SemanticLevel.Partial,
            "the query must report the degradation the build detected, not recompute Full from Compiled alone");
    }
}
