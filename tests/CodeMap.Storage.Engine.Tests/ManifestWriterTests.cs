namespace CodeMap.Storage.Engine.Tests;

using FluentAssertions;
using Xunit;

public sealed class ManifestWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-manifest-test-{Guid.NewGuid():N}");

    public ManifestWriterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string ManifestPath => Path.Combine(_tempDir, "manifest.json");

    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var original = new BaselineManifest(
            FormatMajor: 2,
            FormatMinor: 0,
            CommitSha: "abcdef0123456789abcdef0123456789abcdef01",
            CreatedAt: new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
            SymbolCount: 100,
            FileCount: 20,
            ProjectCount: 3,
            EdgeCount: 500,
            FactCount: 42,
            NStringIds: 1234,
            Segments: new Dictionary<string, SegmentInfo>
            {
                ["dictionary"] = new("dictionary.seg", "AABBCCDD"),
                ["symbols"] = new("symbols.seg", "11223344"),
            });

        ManifestWriter.Write(ManifestPath, original);
        var loaded = ManifestWriter.Read(ManifestPath);

        loaded.Should().NotBeNull();
        loaded!.FormatMajor.Should().Be(2);
        loaded.FormatMinor.Should().Be(0);
        loaded.CommitSha.Should().Be("abcdef0123456789abcdef0123456789abcdef01");
        loaded.SymbolCount.Should().Be(100);
        loaded.FileCount.Should().Be(20);
        loaded.ProjectCount.Should().Be(3);
        loaded.EdgeCount.Should().Be(500);
        loaded.FactCount.Should().Be(42);
        loaded.NStringIds.Should().Be(1234);
        loaded.Segments.Should().ContainKey("dictionary");
        loaded.Segments["dictionary"].File.Should().Be("dictionary.seg");
        loaded.Segments["dictionary"].Crc32Hex.Should().Be("AABBCCDD");
        loaded.Segments["symbols"].Crc32Hex.Should().Be("11223344");
    }

    [Fact]
    public void Read_NonexistentFile_ReturnsNull()
    {
        ManifestWriter.Read(Path.Combine(_tempDir, "nope.json")).Should().BeNull();
    }

    [Fact]
    public void Write_ProducesValidJson()
    {
        var manifest = new BaselineManifest(
            2, 0, "a" + new string('0', 39),
            DateTimeOffset.UtcNow, 1, 1, 1, 1, 1, 10,
            new Dictionary<string, SegmentInfo>());

        ManifestWriter.Write(ManifestPath, manifest);

        var json = File.ReadAllText(ManifestPath);
        json.Should().Contain("format_major");
        json.Should().Contain("commit_sha");
        json.Should().Contain("\"engine\"");
        json.Should().Contain("\"custom\"");
    }
}
