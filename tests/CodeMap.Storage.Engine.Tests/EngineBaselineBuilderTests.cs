namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

public sealed class EngineBaselineBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-builder-test-{Guid.NewGuid():N}");

    public EngineBaselineBuilderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string StoreDir => Path.Combine(_tempDir, "store");
    private const string CommitSha = "abcdef0123456789abcdef0123456789abcdef01";

    private BaselineBuildInput CreateTestInput()
    {
        var files = new List<ExtractedFile>
        {
            new("file1", FilePath.From("src/MyApp/Foo.cs"), "aabb" + new string('0', 60), "MyApp", "class Foo {}"),
            new("file2", FilePath.From("src/MyApp/Bar.cs"), "ccdd" + new string('0', 60), "MyApp", "class Bar {}"),
            new("file3", FilePath.From("src/MyApp.Tests/FooTests.cs"), "eeff" + new string('0', 60), "MyApp.Tests", "class FooTests {}"),
        };

        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(
                SymbolId.From("T:MyApp.Foo"), "global::MyApp.Foo", SymbolKind.Class,
                "public class Foo", "MyApp", FilePath.From("src/MyApp/Foo.cs"), 1, 10, "public", Confidence.High),
            SymbolCard.CreateMinimal(
                SymbolId.From("M:MyApp.Foo.DoWork"), "global::MyApp.Foo.DoWork", SymbolKind.Method,
                "public void DoWork()", "MyApp", FilePath.From("src/MyApp/Foo.cs"), 3, 8, "public", Confidence.High,
                containingType: "Foo"),
            SymbolCard.CreateMinimal(
                SymbolId.From("T:MyApp.Bar"), "global::MyApp.Bar", SymbolKind.Class,
                "public class Bar", "MyApp", FilePath.From("src/MyApp/Bar.cs"), 1, 5, "public", Confidence.High),
            SymbolCard.CreateMinimal(
                SymbolId.From("M:MyApp.Bar.Process"), "global::MyApp.Bar.Process", SymbolKind.Method,
                "public int Process(string input)", "MyApp", FilePath.From("src/MyApp/Bar.cs"), 2, 4, "internal", Confidence.High,
                containingType: "Bar"),
            SymbolCard.CreateMinimal(
                SymbolId.From("T:MyApp.Tests.FooTests"), "global::MyApp.Tests.FooTests", SymbolKind.Class,
                "public class FooTests", "MyApp.Tests", FilePath.From("src/MyApp.Tests/FooTests.cs"), 1, 10, "public", Confidence.High),
        };

        var refs = new List<ExtractedReference>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), SymbolId.From("M:MyApp.Bar.Process"),
                RefKind.Call, FilePath.From("src/MyApp/Foo.cs"), 5, 5),
            new(SymbolId.From("M:MyApp.Bar.Process"), SymbolId.From("T:MyApp.Foo"),
                RefKind.Instantiate, FilePath.From("src/MyApp/Bar.cs"), 3, 3),
        };

        var facts = new List<ExtractedFact>
        {
            new(SymbolId.From("M:MyApp.Foo.DoWork"), null, FactKind.Route,
                "GET|/api/foo", FilePath.From("src/MyApp/Foo.cs"), 4, 4, Confidence.High),
            new(SymbolId.From("M:MyApp.Bar.Process"), null, FactKind.Config,
                "App:MaxRetries|GetValue", FilePath.From("src/MyApp/Bar.cs"), 3, 3, Confidence.High),
        };

        return new BaselineBuildInput(
            CommitSha, @"C:\repo", symbols, files, refs, facts, []);
    }

    [Fact]
    public async Task Build_ProducesAllSegmentFiles()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();

        var baselineDir = result.BaselinePath;
        File.Exists(Path.Combine(baselineDir, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "dictionary.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "content.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "symbols.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "files.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "projects.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "edges.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "facts.seg")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "adjacency-out.idx")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "adjacency-in.idx")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "search.idx")).Should().BeTrue();
        File.Exists(Path.Combine(baselineDir, "checksums.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Build_ManifestHasCorrectCounts()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        var manifest = ManifestWriter.Read(Path.Combine(result.BaselinePath, "manifest.json"));
        manifest.Should().NotBeNull();
        manifest!.CommitSha.Should().Be(CommitSha);
        manifest.FormatMajor.Should().Be(2);
        manifest.SymbolCount.Should().Be(5);
        manifest.FileCount.Should().Be(3);
        manifest.ProjectCount.Should().Be(2); // MyApp + MyApp.Tests
        manifest.EdgeCount.Should().Be(2);
        manifest.FactCount.Should().Be(2);
        manifest.NStringIds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Build_ResultCountsMatch()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        result.SymbolCount.Should().Be(5);
        result.FileCount.Should().Be(3);
        result.EdgeCount.Should().Be(2);
        result.FactCount.Should().Be(2);
    }

    [Fact]
    public async Task Build_SymbolRecordsReadable()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        using var reader = new SegmentReader<SymbolRecord>(Path.Combine(result.BaselinePath, "symbols.seg"));
        reader.Count.Should().Be(5);

        // First symbol should be a Class (kind=1 in v2 spec)
        var first = reader[0];
        first.SymbolIntId.Should().Be(1);
        first.Kind.Should().Be(1); // Class
    }

    [Fact]
    public async Task Build_EdgeRecordsReadable()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        using var reader = new SegmentReader<EdgeRecord>(Path.Combine(result.BaselinePath, "edges.seg"));
        reader.Count.Should().Be(2);

        var first = reader[0];
        first.EdgeKind.Should().Be(1); // Call
        first.FromSymbolIntId.Should().BeGreaterThan(0);
        first.ToSymbolIntId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Build_TempDirectoryCleanedUpOnSuccess()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        var tempDir = Path.Combine(StoreDir, "temp");
        if (Directory.Exists(tempDir))
        {
            Directory.GetDirectories(tempDir).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Build_BaselineExistsAfterBuild()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        var manifestPath = Path.Combine(result.BaselinePath, "manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        ManifestWriter.Read(manifestPath).Should().NotBeNull();
    }

    [Fact]
    public async Task Build_CrcInManifestMatchesActualFiles()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        var manifest = ManifestWriter.Read(Path.Combine(result.BaselinePath, "manifest.json"))!;

        foreach (var (name, info) in manifest.Segments)
        {
            var filePath = Path.Combine(result.BaselinePath, info.File);
            if (!File.Exists(filePath)) continue;
            var actualCrc = ChecksumWriter.ComputeCrc32Hex(filePath);
            info.Crc32Hex.Should().Be(actualCrc, $"CRC mismatch for segment '{name}'");
        }
    }

    [Fact]
    public async Task Build_ContentSegmentHasFileContent()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        using var contentReader = new ContentSegmentReader(Path.Combine(result.BaselinePath, "content.seg"));
        contentReader.Count.Should().Be(3); // 3 files with content
        contentReader.ResolveContent(1).Should().Be("class Foo {}");
        contentReader.ResolveContent(2).Should().Be("class Bar {}");
    }

    [Fact]
    public async Task Build_DictionaryResolvesStrings()
    {
        var builder = new EngineBaselineBuilder(StoreDir);
        var result = await builder.BuildAsync(CreateTestInput(), CancellationToken.None);

        using var dict = new DictionaryReader(Path.Combine(result.BaselinePath, "dictionary.seg"));
        dict.Count.Should().BeGreaterThan(0);

        // Should be able to find FQN strings
        dict.TryFind("T:MyApp.Foo", out _).Should().BeTrue();
        dict.TryFind("M:MyApp.Foo.DoWork", out _).Should().BeTrue();
    }
}
