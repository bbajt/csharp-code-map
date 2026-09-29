namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-08 T02: with 2+ <c>kinds</c>, <see cref="CustomSymbolStore.SearchSymbolsAsync"/> let the engine
/// cut the result to <c>limit</c> across <em>all</em> kinds and filtered afterwards, so matches of the
/// requested kinds were lost whenever other kinds scored higher (same family as GH #6, v2.8.1).
/// Data: "Widget" matches five exact-name methods (score ≥ 100) and one property + one field by prefix.
/// </summary>
public sealed class CustomSymbolStoreMultiKindSearchTests : IAsyncLifetime
{
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef02");
    private static readonly RepoId Repo = RepoId.From("multikind-repo");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-multikind-{Guid.NewGuid():N}");
    private CustomSymbolStore _store = null!;

    /// <summary>Builds the baseline through the real store.</summary>
    public async ValueTask InitializeAsync()
    {
        _store = new CustomSymbolStore(_tempDir);
        await _store.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", CancellationToken.None);
    }

    /// <summary>Releases the store and removes the temp dir.</summary>
    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    private Task<IReadOnlyList<SymbolSearchHit>> Search(IReadOnlyList<SymbolKind>? kinds, int limit) =>
        _store.SearchSymbolsAsync(Repo, Sha, "Widget",
            kinds is null ? null : new SymbolSearchFilters(Kinds: kinds), limit, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Search_TwoKindsOutscoredByAThirdKind_ReturnsTheRequestedKinds()
    {
        var hits = await Search([SymbolKind.Property, SymbolKind.Field], limit: 3);

        hits.Select(h => h.SymbolId.Value).Should().BeEquivalentTo(
            ["P:App.Panel.WidgetCount", "F:App.Panel.WidgetName"],
            "both match and the limit (3) leaves room; the higher-scored methods must not use it up");
    }

    [Fact]
    public async Task Search_TwoKinds_LimitCountsOnlyRequestedKinds()
    {
        var hits = await Search([SymbolKind.Property, SymbolKind.Field], limit: 1);

        hits.Should().ContainSingle().Which.Kind.Should().BeOneOf(SymbolKind.Property, SymbolKind.Field);
    }

    [Fact]
    public async Task Search_SingleKind_Unchanged()
    {
        (await Search([SymbolKind.Method], limit: 3)).Should().HaveCount(3)
            .And.OnlyContain(h => h.Kind == SymbolKind.Method);
        (await Search([SymbolKind.Property], limit: 3)).Select(h => h.SymbolId.Value)
            .Should().Equal("P:App.Panel.WidgetCount");
    }

    [Fact]
    public async Task Search_NoKinds_TopScoresWin()
    {
        (await Search(null, limit: 3)).Should().HaveCount(3).And.OnlyContain(h => h.Kind == SymbolKind.Method);
    }

    private static CompilationResult Data()
    {
        var files = new List<ExtractedFile>();
        var symbols = new List<SymbolCard>();
        for (var i = 1; i <= 5; i++)
        {
            var path = FilePath.From($"src/App/C{i}.cs");
            files.Add(new($"f{i}", path, i.ToString("x2") + new string('0', 62), "App",
                $"public class C{i} {{ public void Widget() {{ }} }}"));
            symbols.Add(SymbolCard.CreateMinimal(SymbolId.From($"M:App.C{i}.Widget"), $"global::App.C{i}.Widget",
                SymbolKind.Method, "public void Widget()", "App", path, 1, 1, "public", Confidence.High,
                containingType: $"C{i}"));
        }

        var panel = FilePath.From("src/App/Panel.cs");
        files.Add(new("fp", panel, "ff" + new string('0', 62), "App",
            "public class Panel { public int WidgetCount { get; } public string WidgetName = \"\"; }"));
        symbols.Add(SymbolCard.CreateMinimal(SymbolId.From("P:App.Panel.WidgetCount"), "global::App.Panel.WidgetCount",
            SymbolKind.Property, "public int WidgetCount", "App", panel, 1, 1, "public", Confidence.High,
            containingType: "Panel"));
        symbols.Add(SymbolCard.CreateMinimal(SymbolId.From("F:App.Panel.WidgetName"), "global::App.Panel.WidgetName",
            SymbolKind.Field, "public string WidgetName", "App", panel, 1, 1, "public", Confidence.High,
            containingType: "Panel"));

        return new CompilationResult(symbols, [], files,
            new IndexStats(symbols.Count, 0, files.Count, 0.0, Confidence.High));
    }
}
