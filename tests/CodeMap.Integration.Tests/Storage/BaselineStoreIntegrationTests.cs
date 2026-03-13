namespace CodeMap.Integration.Tests.Storage;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Integration")]
public class BaselineStoreIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _repoDir;
    private readonly BaselineStore _store;

    private static readonly RepoId Repo = RepoId.From("integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('b', 40));

    public BaselineStoreIntegrationTests()
    {
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private Core.Interfaces.CompilationResult MakeData()
    {
        // Create a real source file on disk
        var srcFile = Path.Combine(_repoDir, "src", "OrderService.cs");
        File.WriteAllLines(srcFile, Enumerable.Range(1, 100)
            .Select(i => i == 10 ? "    public class OrderService { }" : $"    // line {i}"));

        var file = new Core.Interfaces.ExtractedFile(
            FileId: "deadbeef12345678",
            Path: FilePath.From("src/OrderService.cs"),
            Sha256Hash: new string('0', 64),
            ProjectName: "SampleApp");

        var symbol = Core.Models.SymbolCard.CreateMinimal(
            symbolId: SymbolId.From("SampleApp.OrderService"),
            fullyQualifiedName: "SampleApp.OrderService",
            kind: SymbolKind.Class,
            signature: "OrderService()",
            @namespace: "SampleApp",
            filePath: FilePath.From("src/OrderService.cs"),
            spanStart: 10,
            spanEnd: 10,
            visibility: "public",
            confidence: Confidence.High,
            documentation: "Handles orders.");

        var refRow = new Core.Interfaces.ExtractedReference(
            FromSymbol: SymbolId.From("SampleApp.Program"),
            ToSymbol: SymbolId.From("SampleApp.OrderService"),
            Kind: RefKind.Instantiate,
            FilePath: FilePath.From("src/OrderService.cs"),
            LineStart: 5,
            LineEnd: 5);

        return new Core.Interfaces.CompilationResult(
            Symbols: [symbol],
            References: [refRow],
            Files: [file],
            Stats: new Core.Models.IndexStats(1, 1, 1, 0.1, Confidence.High));
    }

    [Fact]
    public async Task RoundTrip_CreateAndRead_SymbolsMatchInput()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var card = await _store.GetSymbolAsync(Repo, Sha, SymbolId.From("SampleApp.OrderService"));

        card.Should().NotBeNull();
        card!.FullyQualifiedName.Should().Be("SampleApp.OrderService");
        card.Kind.Should().Be(SymbolKind.Class);
        card.Documentation.Should().Be("Handles orders.");
        card.SpanStart.Should().Be(10);
        card.Visibility.Should().Be("public");
    }

    [Fact]
    public async Task RoundTrip_CreateAndSearch_FtsFindsSymbol()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "OrderService", null, 20);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedName.Contains("OrderService"));
    }

    [Fact]
    public async Task RoundTrip_CreateAndSearch_DocumentationSearchable()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        // FTS5 should search documentation text too
        var hits = await _store.SearchSymbolsAsync(Repo, Sha, "orders", null, 20);
        hits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_CreateAndGetRefs_RefsMatchInput()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var refs = await _store.GetReferencesAsync(
            Repo, Sha, SymbolId.From("SampleApp.OrderService"), null, 50);

        refs.Should().HaveCount(1);
        refs[0].Kind.Should().Be(RefKind.Instantiate);
        refs[0].FromSymbol.Value.Should().Be("SampleApp.Program");
    }

    [Fact]
    public async Task RoundTrip_CreateAndGetFileSpan_ReturnsCorrectLines()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var span = await _store.GetFileSpanAsync(
            Repo, Sha, FilePath.From("src/OrderService.cs"), 1, 5);

        span.Should().NotBeNull();
        span!.StartLine.Should().Be(1);
        span.EndLine.Should().Be(5);
        span.TotalFileLines.Should().Be(100);
        span.Content.Should().Contain("1 |");
    }

    [Fact]
    public async Task BaselineExists_AfterCreate_ReturnsTrue()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);
        (await _store.BaselineExistsAsync(Repo, Sha)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateBaseline_IsIdempotent_CalledTwiceSucceeds()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var act = async () => await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetFileSpan_LargeFile_TruncatesAt400Lines()
    {
        // Write a 500-line file
        var srcFile = Path.Combine(_repoDir, "src", "BigFile.cs");
        File.WriteAllLines(srcFile, Enumerable.Range(1, 500).Select(i => $"// line {i}"));

        var file = new Core.Interfaces.ExtractedFile(
            "bigfile00000000ff", FilePath.From("src/BigFile.cs"), new string('0', 64), null);
        var symbol = Core.Models.SymbolCard.CreateMinimal(
            SymbolId.From("BigSym"), "BigSym", SymbolKind.Class, "BigSym()",
            "NS", FilePath.From("src/BigFile.cs"), 1, 500, "public", Confidence.High);

        var data = new Core.Interfaces.CompilationResult(
            [symbol], [], [file],
            new Core.Models.IndexStats(1, 0, 1, 0.1, Confidence.High));
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var span = await _store.GetFileSpanAsync(
            Repo, Sha, FilePath.From("src/BigFile.cs"), 1, 500);

        span.Should().NotBeNull();
        span!.Truncated.Should().BeTrue();
        span.Content.Split('\n').Length.Should().Be(400);
    }

    [Fact]
    public async Task GetFileSpan_FormatsWithLineNumbers()
    {
        var data = MakeData();
        await _store.CreateBaselineAsync(Repo, Sha, data, _repoDir);

        var span = await _store.GetFileSpanAsync(
            Repo, Sha, FilePath.From("src/OrderService.cs"), 1, 3);

        span!.Content.Should().MatchRegex(@"^\s+1 \|");
        span.Content.Should().Contain(" 2 |");
        span.Content.Should().Contain(" 3 |");
    }
}
