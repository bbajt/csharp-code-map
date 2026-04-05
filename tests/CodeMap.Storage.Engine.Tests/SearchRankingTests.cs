namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

public sealed class SearchRankingTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-rank-test-{Guid.NewGuid():N}");
    private EngineBaselineReader _reader = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(storeDir);
        var result = await builder.BuildAsync(TestData.CreateTestInput(), CancellationToken.None);
        _reader = new EngineBaselineReader(result.BaselinePath);
        _reader.InitSearch(new SearchIndexReader(_reader, Path.Combine(result.BaselinePath, "search.idx")));
        _reader.InitAdjacency(new AdjacencyIndexReader(
            Path.Combine(result.BaselinePath, "adjacency-out.idx"),
            Path.Combine(result.BaselinePath, "adjacency-in.idx"),
            _reader.SymbolCount));
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ExactDisplayNameMatch_ScoresHighest()
    {
        var results = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(Limit: 50));
        results.Should().NotBeEmpty();

        // "Foo" exact match should score higher than "DoWork" or other partial matches
        var fooResult = results.FirstOrDefault(r =>
            _reader.ResolveString(r.Symbol.DisplayNameStringId) == "Foo");
        fooResult.Score.Should().BeGreaterThanOrEqualTo(100);
    }

    [Fact]
    public void PrefixMatch_ScoresLowerThanExact()
    {
        // Search "Bar" — exact match for Bar class
        var barResults = _reader.Search.SearchSymbols("Bar", new SymbolSearchFilter(Limit: 50));
        var barExact = barResults.FirstOrDefault(r =>
            _reader.ResolveString(r.Symbol.DisplayNameStringId) == "Bar");

        // Search "Ba" — prefix match for Bar
        var prefixResults = _reader.Search.SearchSymbols("Ba", new SymbolSearchFilter(Limit: 50));
        var barPrefix = prefixResults.FirstOrDefault(r =>
            _reader.ResolveString(r.Symbol.DisplayNameStringId) == "Bar");

        if (barExact.Score > 0 && barPrefix.Score > 0)
            barExact.Score.Should().BeGreaterThanOrEqualTo(barPrefix.Score);
    }

    [Fact]
    public void KindFilter_ExcludesNonMatching()
    {
        // Search all, then filter by Method only
        var allResults = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(Limit: 50));
        var methodResults = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(Kind: 8, Limit: 50)); // 8 = Method

        // Method filter should return fewer or equal results
        methodResults.Length.Should().BeLessThanOrEqualTo(allResults.Length);
        // All results should be methods
        foreach (var r in methodResults)
            r.Symbol.Kind.Should().Be(8);
    }

    [Fact]
    public void NamespaceFilter_Works()
    {
        var nsResults = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(NamespacePrefix: "MyApp", Limit: 50));
        nsResults.Should().NotBeEmpty();

        // All results should have namespace starting with MyApp
        foreach (var r in nsResults)
        {
            var ns = _reader.ResolveString(r.Symbol.NamespaceStringId);
            ns.Should().StartWith("MyApp");
        }
    }

    [Fact]
    public void EmptyQuery_ReturnsEmpty()
    {
        var results = _reader.Search.SearchSymbols("", new SymbolSearchFilter(Limit: 50));
        results.Should().BeEmpty();
    }

    [Fact]
    public void NonexistentQuery_ReturnsEmpty()
    {
        var results = _reader.Search.SearchSymbols("ZZZZNOTFOUND", new SymbolSearchFilter(Limit: 50));
        results.Should().BeEmpty();
    }

    [Fact]
    public void ResultsAreSortedByScoreDescending()
    {
        var results = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(Limit: 50));
        if (results.Length > 1)
        {
            for (var i = 1; i < results.Length; i++)
                results[i].Score.Should().BeLessThanOrEqualTo(results[i - 1].Score);
        }
    }

    [Fact]
    public void LimitRespected()
    {
        var results = _reader.Search.SearchSymbols("MyApp", new SymbolSearchFilter(Limit: 2));
        results.Length.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void TextSearch_FindsContent()
    {
        var (matches, _) = _reader.Search.SearchText("class Foo", new TextSearchFilter(Limit: 100));
        matches.Should().NotBeEmpty();
        matches.Should().Contain(m => m.FilePath.Contains("Foo.cs"));
    }

    [Fact]
    public void TextSearch_FileGlobFilter()
    {
        var (matches, _) = _reader.Search.SearchText("class", new TextSearchFilter(FileGlob: "*.cs", Limit: 100));
        foreach (var m in matches)
            m.FilePath.Should().EndWith(".cs");
    }

    [Fact]
    public void TextSearch_LimitTruncates()
    {
        var (matches, truncated) = _reader.Search.SearchText("public", new TextSearchFilter(Limit: 1));
        matches.Length.Should().Be(1);
        // May or may not be truncated depending on total matches
    }
}
