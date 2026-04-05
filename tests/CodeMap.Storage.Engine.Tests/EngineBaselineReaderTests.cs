namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// Integration tests: build a baseline from test data, then open reader + search + adjacency
/// and verify all query paths work correctly.
/// </summary>
public sealed class EngineBaselineReaderTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-reader-test-{Guid.NewGuid():N}");
    private string _baselineDir = "";
    private EngineBaselineReader _reader = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(storeDir);

        var input = CreateTestInput();
        var result = await builder.BuildAsync(input, CancellationToken.None);
        result.Success.Should().BeTrue(result.ErrorMessage ?? "");
        _baselineDir = result.BaselinePath;

        _reader = new EngineBaselineReader(_baselineDir);
        _reader.InitSearch(new SearchIndexReader(_reader, Path.Combine(_baselineDir, "search.idx")));
        _reader.InitAdjacency(new AdjacencyIndexReader(
            Path.Combine(_baselineDir, "adjacency-out.idx"),
            Path.Combine(_baselineDir, "adjacency-in.idx"),
            _reader.SymbolCount));
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — Windows file locks may persist briefly after mmap dispose
        }
        return ValueTask.CompletedTask;
    }

    private static BaselineBuildInput CreateTestInput()
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/App/Foo.cs"), "aa" + new string('0', 62), "MyApp", "public class Foo { public void DoWork() { } }"),
            new("f2", FilePath.From("src/App/Bar.cs"), "bb" + new string('0', 62), "MyApp", "public class Bar { public int Process(string x) { return 0; } }"),
            new("f3", FilePath.From("src/App/IService.cs"), "cc" + new string('0', 62), "MyApp", "public interface IService { void Run(); }"),
        };

        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Foo"), "global::MyApp.Foo", SymbolKind.Class,
                "public class Foo", "MyApp", FilePath.From("src/App/Foo.cs"), 1, 10, "public", Confidence.High),
            SymbolCard.CreateMinimal(SymbolId.From("M:MyApp.Foo.DoWork"), "global::MyApp.Foo.DoWork", SymbolKind.Method,
                "public void DoWork()", "MyApp", FilePath.From("src/App/Foo.cs"), 3, 8, "public", Confidence.High,
                containingType: "Foo"),
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.Bar"), "global::MyApp.Bar", SymbolKind.Class,
                "public class Bar", "MyApp", FilePath.From("src/App/Bar.cs"), 1, 5, "public", Confidence.High),
            SymbolCard.CreateMinimal(SymbolId.From("M:MyApp.Bar.Process"), "global::MyApp.Bar.Process", SymbolKind.Method,
                "public int Process(string x)", "MyApp", FilePath.From("src/App/Bar.cs"), 2, 4, "internal", Confidence.High,
                containingType: "Bar"),
            SymbolCard.CreateMinimal(SymbolId.From("T:MyApp.IService"), "global::MyApp.IService", SymbolKind.Interface,
                "public interface IService", "MyApp", FilePath.From("src/App/IService.cs"), 1, 3, "public", Confidence.High),
        };

        var refs = new List<ExtractedReference>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), SymbolId.From("M:MyApp.Bar.Process"),
                RefKind.Call, FilePath.From("src/App/Foo.cs"), 5, 5),
            new(SymbolId.From("T:MyApp.Foo"), SymbolId.From("T:MyApp.IService"),
                RefKind.Implementation, FilePath.From("src/App/Foo.cs"), 1, 1),
        };

        var facts = new List<ExtractedFact>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), null, FactKind.Route,
                "GET|/api/foo", FilePath.From("src/App/Foo.cs"), 4, 4, Confidence.High),
        };

        return new BaselineBuildInput("abcdef0123456789abcdef0123456789abcdef01", @"C:\repo", symbols, files, refs, facts, []);
    }

    // ── Reader basics ────────────────────────────────────────────────────────

    [Fact]
    public void Counts_MatchInput()
    {
        _reader.SymbolCount.Should().Be(5);
        _reader.FileCount.Should().Be(3);
        _reader.EdgeCount.Should().Be(2);
        _reader.FactCount.Should().Be(1);
        _reader.ProjectCount.Should().Be(1);
    }

    [Fact]
    public void GetSymbolByFqn_ReturnsCorrectRecord()
    {
        var rec = _reader.GetSymbolByFqn("T:MyApp.Foo");
        rec.Should().NotBeNull();
        rec!.Value.Kind.Should().Be(1); // Class
    }

    [Fact]
    public void GetSymbolByStableId_ReturnsCorrectRecord()
    {
        // Find the stable id for Foo
        var fooRec = _reader.GetSymbolByFqn("T:MyApp.Foo");
        fooRec.Should().NotBeNull();
        var stableId = _reader.ResolveString(fooRec!.Value.StableIdStringId);
        stableId.Should().StartWith("sym_");

        var byStable = _reader.GetSymbolByStableId(stableId);
        byStable.Should().NotBeNull();
        byStable!.Value.SymbolIntId.Should().Be(fooRec.Value.SymbolIntId);
    }

    [Fact]
    public void GetSymbolByFqn_NotFound_ReturnsNull()
    {
        _reader.GetSymbolByFqn("T:DoesNotExist").Should().BeNull();
    }

    [Fact]
    public void GetSymbolsByFile_ReturnsCorrectSet()
    {
        var file = _reader.GetFileByPath("src/App/Foo.cs");
        file.Should().NotBeNull();
        var symbols = _reader.GetSymbolsByFile(file!.Value.FileIntId);
        symbols.Count.Should().Be(2); // Foo class + DoWork method
    }

    [Fact]
    public void GetFileByPath_CaseInsensitive()
    {
        _reader.GetFileByPath("SRC/APP/FOO.CS").Should().NotBeNull();
    }

    [Fact]
    public void GetFactsBySymbol_ReturnsCorrectFacts()
    {
        var doWork = _reader.GetSymbolByFqn("M:MyApp.Foo.DoWork");
        doWork.Should().NotBeNull();
        var facts = _reader.GetFactsBySymbol(doWork!.Value.SymbolIntId);
        facts.Count.Should().Be(1);
        facts[0].FactKind.Should().Be(0); // Route
    }

    [Fact]
    public void GetFactsByKind_ReturnsCorrectFacts()
    {
        var routeFacts = _reader.GetFactsByKind(0); // Route
        routeFacts.Count.Should().Be(1);
    }

    // ── Adjacency ────────────────────────────────────────────────────────────

    [Fact]
    public void GetOutgoingEdges_ReturnsCallEdge()
    {
        var doWork = _reader.GetSymbolByFqn("M:MyApp.Foo.DoWork");
        doWork.Should().NotBeNull();
        var edges = _reader.GetOutgoingEdges(doWork!.Value.SymbolIntId);
        edges.Count.Should().BeGreaterThanOrEqualTo(1);
        edges.Should().Contain(e => e.EdgeKind == 1); // Call
    }

    [Fact]
    public void GetIncomingEdges_BarProcess_HasCaller()
    {
        var process = _reader.GetSymbolByFqn("M:MyApp.Bar.Process");
        process.Should().NotBeNull();
        var edges = _reader.GetIncomingEdges(process!.Value.SymbolIntId);
        edges.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void EdgeFilter_ByKind_Works()
    {
        var doWork = _reader.GetSymbolByFqn("M:MyApp.Foo.DoWork");
        doWork.Should().NotBeNull();
        var callEdges = _reader.GetOutgoingEdges(doWork!.Value.SymbolIntId, new EdgeFilter(EdgeKind: 1));
        callEdges.Count.Should().BeGreaterThanOrEqualTo(1);
        var implEdges = _reader.GetOutgoingEdges(doWork.Value.SymbolIntId, new EdgeFilter(EdgeKind: 5));
        implEdges.Count.Should().Be(0); // DoWork doesn't have implements edges
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public void SearchSymbols_ExactName_ReturnsResult()
    {
        var results = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(Limit: 10));
        results.Should().NotBeEmpty();
        results.Should().Contain(r =>
            _reader.ResolveString(r.Symbol.DisplayNameStringId) == "Foo");
    }

    [Fact]
    public void SearchSymbols_CamelCasePartial_ReturnsResult()
    {
        var results = _reader.Search.SearchSymbols("DoWork", new SymbolSearchFilter(Limit: 10));
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void SearchSymbols_KindFilter_Excludes()
    {
        var results = _reader.Search.SearchSymbols("Foo", new SymbolSearchFilter(Kind: 8, Limit: 10)); // Method only
        // Foo is a Class (1), should not appear
        results.Should().NotContain(r =>
            _reader.ResolveString(r.Symbol.DisplayNameStringId) == "Foo"
            && r.Symbol.Kind == 1);
    }

    [Fact]
    public void SearchText_FindsMatchInContent()
    {
        var (matches, _) = _reader.Search.SearchText("DoWork", new TextSearchFilter(Limit: 100));
        matches.Should().NotBeEmpty();
        matches.Should().Contain(m => m.FilePath == "src/App/Foo.cs");
    }

    [Fact]
    public void SearchText_NoMatch_ReturnsEmpty()
    {
        var (matches, _) = _reader.Search.SearchText("ZZZZNOTFOUND", new TextSearchFilter(Limit: 100));
        matches.Should().BeEmpty();
    }

    // ── Merged reader ────────────────────────────────────────────────────────

    [Fact]
    public void MergedReader_DelegatesToBaseline()
    {
        var merged = new EngineMergedReader(_reader);
        var sym = merged.GetSymbolByFqn("T:MyApp.Bar");
        sym.Should().NotBeNull();

        var files = merged.EnumerateFilePaths().ToList();
        files.Count.Should().Be(3);
    }

    // ── CustomSymbolStore integration ────────────────────────────────────────

    private async Task<CustomSymbolStore> CreateStoreWithBaseline()
    {
        var store = new CustomSymbolStore(Path.Combine(_tempDir, "store"));
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        if (!await store.BaselineExistsAsync(repoId, sha))
        {
            var input = CreateTestInput();
            var data = new CompilationResult(input.Symbols, input.References, input.Files,
                new IndexStats(input.Symbols.Count, input.References.Count, input.Files.Count, 0.0, Confidence.High),
                Facts: input.Facts);
            await store.CreateBaselineAsync(repoId, sha, data, @"C:\repo");
        }
        return store;
    }

    [Fact]
    public async Task CustomSymbolStore_GetSymbolAsync_Works()
    {
        var store = await CreateStoreWithBaseline();
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        var card = await store.GetSymbolAsync(repoId, sha, SymbolId.From("T:MyApp.Foo"));
        card.Should().NotBeNull();
        card!.Kind.Should().Be(SymbolKind.Class);
        card.Namespace.Should().Be("MyApp");
    }

    [Fact]
    public async Task CustomSymbolStore_SearchSymbolsAsync_Works()
    {
        var store = await CreateStoreWithBaseline();
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        var hits = await store.SearchSymbolsAsync(repoId, sha, "Bar", null, 10);
        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedName.Contains("Bar"));
    }

    [Fact]
    public async Task CustomSymbolStore_GetReferencesAsync_Works()
    {
        var store = await CreateStoreWithBaseline();
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        var refs = await store.GetReferencesAsync(repoId, sha, SymbolId.From("M:MyApp.Bar.Process"), null, 100);
        refs.Count.Should().BeGreaterThanOrEqualTo(1); // Called by DoWork
    }

    [Fact]
    public async Task CustomSymbolStore_GetFactsByKindAsync_Works()
    {
        var store = await CreateStoreWithBaseline();
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        var facts = await store.GetFactsByKindAsync(repoId, sha, FactKind.Route, 100);
        facts.Count.Should().Be(1);
        facts[0].Value.Should().Contain("GET");
    }

    [Fact]
    public async Task CustomSymbolStore_GetAllFilePathsAsync_Works()
    {
        var store = await CreateStoreWithBaseline();
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        var paths = await store.GetAllFilePathsAsync(repoId, sha);
        paths.Count.Should().Be(3);
    }

    [Fact]
    public async Task CustomSymbolStore_GetFileSpanAsync_Works()
    {
        var store = await CreateStoreWithBaseline();
        var sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
        var repoId = RepoId.From("test-repo");

        var span = await store.GetFileSpanAsync(repoId, sha, FilePath.From("src/App/Foo.cs"), 1, 1);
        span.Should().NotBeNull();
        span!.Content.Should().Contain("class Foo");
    }
}
