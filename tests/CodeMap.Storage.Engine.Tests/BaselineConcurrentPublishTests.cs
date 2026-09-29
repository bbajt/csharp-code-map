namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// Regression tests for F6 (PHASE-21-02 T01): publishing a baseline must never delete or
/// overwrite a baseline that is already published — another process may have it
/// memory-mapped — and an incomplete ("gutted") baseline must never be served.
/// Each <see cref="CustomSymbolStore"/> instance stands in for one daemon process.
/// </summary>
public sealed class BaselineConcurrentPublishTests : IDisposable
{
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");
    private static readonly RepoId Repo = RepoId.From("f6-repo");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-f6-{Guid.NewGuid():N}");
    private readonly List<CustomSymbolStore> _stores = [];

    private string StoreDir => Path.Combine(_tempDir, "store");

    private string BaselineDir => Path.Combine(StoreDir, "f6-repo", "baselines", Sha.Value);

    /// <summary>Disposes every store (releasing mmaps) and removes the temp dir.</summary>
    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task CreateBaseline_WhileAnotherStoreHasBaselineOpen_Succeeds_AndReaderStillServes()
    {
        // "Process A" publishes and queries the baseline — its segments are now memory-mapped.
        var a = NewStore();
        await a.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);
        var before = await a.SearchSymbolsAsync(Repo, Sha, "Bar", null, 10, TestContext.Current.CancellationToken);
        before.Should().NotBeEmpty();
        (await a.GetSymbolAsync(Repo, Sha, SymbolId.From("T:MyApp.Foo"), TestContext.Current.CancellationToken))
            .Should().NotBeNull();

        // "Process B" built the same (repo, commit) concurrently and now publishes.
        var filesBefore = Directory.GetFiles(BaselineDir).Select(Path.GetFileName).Order().ToList();
        var b = NewStore();
        var publishError = await Record.ExceptionAsync(
            () => b.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken));

        // The published baseline must be untouched (pre-fix, B's delete guts it before failing).
        Directory.GetFiles(BaselineDir).Select(Path.GetFileName).Order()
            .Should().Equal(filesBefore, "a published baseline must never be modified by another publisher");
        publishError.Should().BeNull("a concurrent publisher must adopt the existing baseline, not delete it");

        // A's open reader is unaffected, the baseline is still complete, and a fresh reader sees it.
        var after = await a.SearchSymbolsAsync(Repo, Sha, "Bar", null, 10, TestContext.Current.CancellationToken);
        after.Select(h => h.SymbolId).Should().BeEquivalentTo(before.Select(h => h.SymbolId));
        (await NewStore().BaselineExistsAsync(Repo, Sha, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await NewStore().SearchSymbolsAsync(Repo, Sha, "Bar", null, 10, TestContext.Current.CancellationToken))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateBaseline_TwoStoresInParallel_BothSucceed_OneCompleteBaseline()
    {
        for (var i = 0; i < 10; i++)
        {
            ResetStoreDir();
            var s1 = NewStore();
            var s2 = NewStore();

            var t1 = s1.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);
            var t2 = s2.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);
            var both = () => Task.WhenAll(t1, t2);
            await both.Should().NotThrowAsync($"iteration {i}: concurrent publishers must converge");

            (await s1.SearchSymbolsAsync(Repo, Sha, "Foo", null, 10, TestContext.Current.CancellationToken)).Should().NotBeEmpty();
            (await s2.SearchSymbolsAsync(Repo, Sha, "Foo", null, 10, TestContext.Current.CancellationToken)).Should().NotBeEmpty();
            Directory.GetDirectories(Path.Combine(StoreDir, "f6-repo", "baselines")).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task BaselineExists_GuttedBaselineWithManifest_ReturnsFalse()
    {
        var a = NewStore();
        await a.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);
        a.Dispose();
        _stores.Remove(a);

        // The F6 failure shape on Windows: some segments deleted, manifest survived.
        File.Delete(Path.Combine(BaselineDir, "search.idx"));
        File.Exists(Path.Combine(BaselineDir, "manifest.json")).Should().BeTrue();

        (await NewStore().BaselineExistsAsync(Repo, Sha, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Query_GuttedBaseline_ThrowsCorruption_NotEmptyResults()
    {
        var a = NewStore();
        await a.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);
        a.Dispose();
        _stores.Remove(a);
        File.Delete(Path.Combine(BaselineDir, "search.idx"));

        var query = () => NewStore().SearchSymbolsAsync(Repo, Sha, "Bar", null, 10, TestContext.Current.CancellationToken);

        await query.Should().ThrowAsync<StorageCorruptionException>();
    }

    [Fact]
    public async Task CreateBaseline_OverGuttedBaseline_RebuildsCompleteBaseline()
    {
        var a = NewStore();
        await a.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);
        a.Dispose();
        _stores.Remove(a);
        File.Delete(Path.Combine(BaselineDir, "search.idx"));

        var b = NewStore();
        await b.CreateBaselineAsync(Repo, Sha, Data(), @"C:\repo", TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(BaselineDir, "search.idx")).Should().BeTrue();
        (await b.SearchSymbolsAsync(Repo, Sha, "Bar", null, 10, TestContext.Current.CancellationToken)).Should().NotBeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CustomSymbolStore NewStore()
    {
        var store = new CustomSymbolStore(StoreDir);
        _stores.Add(store);
        return store;
    }

    private void ResetStoreDir()
    {
        foreach (var s in _stores) s.Dispose();
        _stores.Clear();
        if (Directory.Exists(StoreDir)) Directory.Delete(StoreDir, recursive: true);
    }

    private static CompilationResult Data()
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/App/Foo.cs"), "aa" + new string('0', 62), "MyApp", "public class Foo { public void DoWork() { } }"),
            new("f2", FilePath.From("src/App/Bar.cs"), "bb" + new string('0', 62), "MyApp", "public class Bar { public int Process(string x) { return 0; } }"),
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
        };
        var refs = new List<ExtractedReference>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), SymbolId.From("T:MyApp.Bar"),
                RefKind.Call, FilePath.From("src/App/Foo.cs"), 5, 5),
        };
        return new CompilationResult(symbols, refs, files,
            new IndexStats(symbols.Count, refs.Count, files.Count, 0.0, Confidence.High));
    }
}
