namespace CodeMap.Storage.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Storage.Tests.Helpers;
using FluentAssertions;

public class BaselineStoreReadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BaselineStore _store;

    // Test data reused across tests
    private static readonly RepoId Repo = StorageTestHelpers.TestRepo;
    private static readonly CommitSha Sha = StorageTestHelpers.TestSha;

    private static readonly ExtractedFile File1 = StorageTestHelpers.MakeFile("src/Foo.cs", "aaaa000011111111");
    private static readonly ExtractedFile File2 = StorageTestHelpers.MakeFile("src/Bar.cs", "bbbb000022222222");

    private static readonly Core.Models.SymbolCard FooClass = StorageTestHelpers.MakeSymbol(
        "TestNs.Foo", "TestNs.Foo", SymbolKind.Class,
        "src/Foo.cs", spanStart: 5, spanEnd: 50,
        @namespace: "TestNs",
        documentation: "The Foo class does things.");

    private static readonly Core.Models.SymbolCard BarMethod = StorageTestHelpers.MakeSymbol(
        "TestNs.Bar.DoWork", "TestNs.Bar.DoWork", SymbolKind.Method,
        "src/Bar.cs", spanStart: 10, spanEnd: 20,
        @namespace: "TestNs", containingType: "TestNs.Bar",
        documentation: "Does work.");

    private static readonly Core.Models.SymbolCard InternalClass = StorageTestHelpers.MakeSymbol(
        "TestNs.Internal.Impl", "TestNs.Internal.Impl", SymbolKind.Class,
        "src/Foo.cs", @namespace: "TestNs.Internal", visibility: "internal");

    public BaselineStoreReadTests()
    {
        (_store, _tempDir) = StorageTestHelpers.CreateDiskStore();
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        var refRow = StorageTestHelpers.MakeRef(
            "TestNs.Foo", "TestNs.Bar.DoWork", RefKind.Call, "src/Foo.cs", 10, 10);
        var data = StorageTestHelpers.MakeResult(
            [FooClass, BarMethod, InternalClass],
            [refRow],
            [File1, File2]);
        await _store.CreateBaselineAsync(Repo, Sha, data);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── GetSymbolAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbol_ExistingId_ReturnsSymbolCard()
    {
        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("TestNs.Foo"));
        card.Should().NotBeNull();
        card!.FullyQualifiedName.Should().Be("TestNs.Foo");
    }

    [Fact]
    public async Task GetSymbol_NonExistentId_ReturnsNull()
    {
        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("NoSuch.Type"));
        card.Should().BeNull();
    }

    [Fact]
    public async Task GetSymbol_ReturnsCorrectKind()
    {
        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("TestNs.Bar.DoWork"));
        card!.Kind.Should().Be(SymbolKind.Method);
    }

    [Fact]
    public async Task GetSymbol_ReturnsCorrectFilePathAndSpan()
    {
        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("TestNs.Foo"));
        card!.FilePath.Value.Should().Be("src/Foo.cs");
        card.SpanStart.Should().Be(5);
        card.SpanEnd.Should().Be(50);
    }

    [Fact]
    public async Task GetSymbol_NullOptionalFieldsMappedCorrectly()
    {
        // FooClass has no containing type
        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("TestNs.Foo"));
        card!.ContainingType.Should().BeNull();
    }

    [Fact]
    public async Task GetSymbol_WithDocumentation_ReturnsDoc()
    {
        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("TestNs.Foo"));
        card!.Documentation.Should().Be("The Foo class does things.");
    }

    [Fact]
    public async Task GetSymbol_NoDatabase_ReturnsNull()
    {
        var missingRepo = RepoId.From("no-such-repo");
        var card = await _store.GetSymbolAsync(missingRepo, Sha, SymbolId.From("TestNs.Foo"));
        card.Should().BeNull();
    }

    // ── SearchSymbolsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task SearchSymbols_MatchingQuery_ReturnsHits()
    {
        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "Foo", null, 20);
        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedName == "TestNs.Foo");
    }

    [Fact]
    public async Task SearchSymbols_NoMatches_ReturnsEmpty()
    {
        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "ZZZNoMatchXXX", null, 20);
        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchSymbols_LimitApplied_ReturnsAtMostLimit()
    {
        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "TestNs", null, 1);
        hits.Count.Should().BeLessOrEqualTo(1);
    }

    [Fact]
    public async Task SearchSymbols_KindFilter_ReturnsOnlyMatchingKinds()
    {
        var filters = new Core.Interfaces.SymbolSearchFilters(
            Kinds: [SymbolKind.Method]);
        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "TestNs", filters, 20);
        hits.Should().AllSatisfy(h => h.Kind.Should().Be(SymbolKind.Method));
    }

    [Fact]
    public async Task SearchSymbols_NamespaceFilter_ReturnsOnlyMatchingNamespace()
    {
        var filters = new Core.Interfaces.SymbolSearchFilters(
            Namespace: "TestNs.Internal");
        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "Impl", filters, 20);
        hits.Should().NotBeEmpty();
        hits.Should().AllSatisfy(h => h.FullyQualifiedName.Should().Contain("Internal"));
    }

    [Fact]
    public async Task SearchSymbols_NoDatabase_ReturnsEmpty()
    {
        var missingRepo = RepoId.From("no-such-repo");
        var hits = await _store.SearchSymbolsAsync(missingRepo, Sha, "Foo", null, 20);
        hits.Should().BeEmpty();
    }

    // ── FTS CamelCase tokenization ───────────────────────────────────────────

    [Fact]
    public async Task SearchSymbols_CamelCaseComponent_FindsSymbol()
    {
        // Searching "Service" should find "IGitService" (camelcase component word).
        var gitSvc = StorageTestHelpers.MakeSymbol(
            "T:TestNs.IGitService", "T:TestNs.IGitService", SymbolKind.Interface, "src/Foo.cs",
            @namespace: "TestNs");
        var data = StorageTestHelpers.MakeResult([gitSvc], [], [File1]);
        await _store.CreateBaselineAsync(Repo, CommitSha.From(new string('e', 40)), data);

        var hits = await _store.SearchSymbolsAsync(
            Repo, CommitSha.From(new string('e', 40)), "Service", null, 20);

        hits.Should().Contain(h => h.FullyQualifiedName.Contains("IGitService"),
            "CamelCase component 'Service' should match IGitService via name_tokens");
    }

    [Fact]
    public async Task SearchSymbols_CamelCasePrefixSearch_StillWorks()
    {
        // Regression: prefix search IGit* must still work after CamelCase change.
        var gitSvc = StorageTestHelpers.MakeSymbol(
            "T:TestNs.IGitService2", "T:TestNs.IGitService2", SymbolKind.Interface, "src/Foo.cs",
            @namespace: "TestNs");
        var data = StorageTestHelpers.MakeResult([gitSvc], [], [File1]);
        await _store.CreateBaselineAsync(Repo, CommitSha.From(new string('f', 40)), data);

        var hits = await _store.SearchSymbolsAsync(
            Repo, CommitSha.From(new string('f', 40)), "IGitService2*", null, 20);

        hits.Should().Contain(h => h.FullyQualifiedName.Contains("IGitService2"),
            "prefix search must still work after CamelCase tokenization addition");
    }

    [Fact]
    public async Task SearchSymbols_CamelCaseMiddleComponent_FindsSymbol()
    {
        // Searching "Symbol" should find "ISymbolStore" (middle component).
        var symStore = StorageTestHelpers.MakeSymbol(
            "T:TestNs.ISymbolStore", "T:TestNs.ISymbolStore", SymbolKind.Interface, "src/Foo.cs",
            @namespace: "TestNs");
        var data = StorageTestHelpers.MakeResult([symStore], [], [File1]);
        await _store.CreateBaselineAsync(Repo, CommitSha.From(new string('d', 40)), data);

        var hits = await _store.SearchSymbolsAsync(
            Repo, CommitSha.From(new string('d', 40)), "Symbol", null, 20);

        hits.Should().Contain(h => h.FullyQualifiedName.Contains("ISymbolStore"),
            "CamelCase middle component 'Symbol' should match ISymbolStore");
    }

    // ── GetReferencesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetReferences_ToSymbol_ReturnsRefs()
    {
        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("TestNs.Bar.DoWork"), null, 50);
        refs.Should().HaveCount(1);
        refs[0].Kind.Should().Be(RefKind.Call);
        refs[0].FromSymbol.Value.Should().Be("TestNs.Foo");
    }

    [Fact]
    public async Task GetReferences_KindFilter_ReturnsOnlyMatchingKind()
    {
        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("TestNs.Bar.DoWork"), RefKind.Call, 50);
        refs.Should().AllSatisfy(r => r.Kind.Should().Be(RefKind.Call));
    }

    [Fact]
    public async Task GetReferences_WrongKindFilter_ReturnsEmpty()
    {
        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("TestNs.Bar.DoWork"), RefKind.Write, 50);
        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReferences_UnknownSymbol_ReturnsEmpty()
    {
        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("NoSuch.Symbol"), null, 50);
        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReferences_LimitApplied_ReturnsAtMostLimit()
    {
        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("TestNs.Bar.DoWork"), null, 0);
        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReferences_ExcerptIsNull()
    {
        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("TestNs.Bar.DoWork"), null, 50);
        refs.Should().AllSatisfy(r => r.Excerpt.Should().BeNull());
    }

    [Fact]
    public async Task GetReferences_NoDatabase_ReturnsEmpty()
    {
        var missingRepo = RepoId.From("no-such-repo");
        var refs = await _store.GetReferencesAsync(
            missingRepo, Sha, SymbolId.From("TestNs.Bar.DoWork"), null, 50);
        refs.Should().BeEmpty();
    }

    // ── GetFileSpanAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetFileSpan_NotIndexedFile_ReturnsNull()
    {
        var span = await _store.GetFileSpanAsync(
            Repo, Sha, FilePath.From("src/NotThere.cs"), 1, 10);
        span.Should().BeNull();
    }

    [Fact]
    public async Task GetFileSpan_NoDatabase_ReturnsNull()
    {
        var missingRepo = RepoId.From("no-such-repo");
        var span = await _store.GetFileSpanAsync(
            missingRepo, Sha, FilePath.From("src/Foo.cs"), 1, 10);
        span.Should().BeNull();
    }

    [Fact]
    public async Task GetFileSpan_IndexedFileWithNoRepoRoot_ReturnsNull()
    {
        // No repoRootPath was passed to CreateBaselineAsync, so it cannot read from disk
        var span = await _store.GetFileSpanAsync(
            Repo, Sha, FilePath.From("src/Foo.cs"), 1, 10);
        // Without repoRoot stored, returns null
        span.Should().BeNull();
    }
}
