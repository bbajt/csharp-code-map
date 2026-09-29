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
/// PHASE-21-08 T02 through the real query stack: with 2+ <c>kinds</c>, the page and <c>truncated</c> must
/// count only symbols of the requested kinds. Before the fix the store filtered kinds after the engine's
/// limit, so a query whose best hits were other kinds returned nothing, with <c>truncated: false</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MultiKindSearchTruncationTests : IDisposable
{
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly RepoId Repo = RepoId.From("multikind-int-repo");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "codemap-multikind-int-" + Guid.NewGuid().ToString("N"));
    private readonly CustomSymbolStore _store;

    /// <summary>Creates a real store over a fresh temp dir.</summary>
    public MultiKindSearchTruncationTests() => _store = new CustomSymbolStore(_tempDir);

    /// <summary>Releases the store and removes the temp dir.</summary>
    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Search_TwoKindsWithMoreMatchesThanTheLimit_ReturnsAPageAndTruncatedTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateBaselineAsync(Repo, Sha, Data(), _tempDir, ct);
        var engine = new QueryEngine(
            _store, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(_store), new GraphTraverser(),
            new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);

        var result = await engine.SearchSymbolsAsync(
            new RoutingContext(repoId: Repo, baselineCommitSha: Sha), "Widget",
            new SymbolSearchFilters(Kinds: [SymbolKind.Property, SymbolKind.Field]),
            new BudgetLimits(maxResults: 1), ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().ContainSingle()
            .Which.Kind.Should().BeOneOf(SymbolKind.Property, SymbolKind.Field);
        result.Value.Data.Truncated.Should().BeTrue("two symbols of the requested kinds match and only one was returned");
    }

    private static CompilationResult Data()
    {
        var files = new List<ExtractedFile>();
        var symbols = new List<SymbolCard>();
        for (var i = 1; i <= 5; i++)
        {
            var path = FilePath.From($"src/App/C{i}.cs");
            files.Add(new($"f{i}", path, i.ToString("x2") + new string('0', 62), "App", $"class C{i} {{ void Widget() {{ }} }}"));
            symbols.Add(SymbolCard.CreateMinimal(SymbolId.From($"M:App.C{i}.Widget"), $"global::App.C{i}.Widget",
                SymbolKind.Method, "void Widget()", "App", path, 1, 1, "public", Confidence.High, containingType: $"C{i}"));
        }
        var panel = FilePath.From("src/App/Panel.cs");
        files.Add(new("fp", panel, "ff" + new string('0', 62), "App", "class Panel { int WidgetCount; string WidgetName; }"));
        symbols.Add(SymbolCard.CreateMinimal(SymbolId.From("P:App.Panel.WidgetCount"), "global::App.Panel.WidgetCount",
            SymbolKind.Property, "int WidgetCount", "App", panel, 1, 1, "public", Confidence.High, containingType: "Panel"));
        symbols.Add(SymbolCard.CreateMinimal(SymbolId.From("F:App.Panel.WidgetName"), "global::App.Panel.WidgetName",
            SymbolKind.Field, "string WidgetName", "App", panel, 1, 1, "public", Confidence.High, containingType: "Panel"));
        return new CompilationResult(symbols, [], files,
            new IndexStats(symbols.Count, 0, files.Count, 0.0, Confidence.High));
    }
}
