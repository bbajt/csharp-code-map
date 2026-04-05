namespace CodeMap.Storage.Engine.Tests;

using System.Text;
using FluentAssertions;
using Xunit;

public sealed class ContentSegmentRoundtripTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-content-test-{Guid.NewGuid():N}");

    public ContentSegmentRoundtripTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string ContentPath => Path.Combine(_tempDir, "content.seg");

    [Fact]
    public void EmptyContent_CountIsZero()
    {
        ContentSegmentWriter.Write(ContentPath, []);
        using var reader = new ContentSegmentReader(ContentPath);
        reader.Count.Should().Be(0);
    }

    [Fact]
    public void VaryingSizes_AllResolveCorrectly()
    {
        var contents = new List<byte[]>
        {
            Array.Empty<byte>(),                                   // 0 bytes
            Encoding.UTF8.GetBytes("short"),                       // 5 bytes
            Encoding.UTF8.GetBytes(new string('x', 1000)),         // 1 KB
            Encoding.UTF8.GetBytes(new string('y', 100_000)),      // 100 KB
        };

        ContentSegmentWriter.Write(ContentPath, contents);
        using var reader = new ContentSegmentReader(ContentPath);

        reader.Count.Should().Be(4);
        reader.ResolveContent(1).Should().BeEmpty();
        reader.ResolveContent(2).Should().Be("short");
        reader.ResolveContent(3).Should().Be(new string('x', 1000));
        reader.ResolveContent(4).Should().Be(new string('y', 100_000));
    }

    [Fact]
    public void ContentIdZero_ReturnsEmptyString()
    {
        ContentSegmentWriter.Write(ContentPath, [Encoding.UTF8.GetBytes("data")]);
        using var reader = new ContentSegmentReader(ContentPath);
        reader.ResolveContent(0).Should().BeEmpty();
    }

    [Fact]
    public void OutOfRange_ThrowsStorageFormatException()
    {
        ContentSegmentWriter.Write(ContentPath, [Encoding.UTF8.GetBytes("one")]);
        using var reader = new ContentSegmentReader(ContentPath);

        var act = () => reader.ResolveContent(99);
        act.Should().Throw<StorageFormatException>();
    }

    [Fact]
    public void UnicodeContent_RoundTrips()
    {
        var unicode = "日本語コンテンツ 🎉 with mixed content";
        ContentSegmentWriter.Write(ContentPath, [Encoding.UTF8.GetBytes(unicode)]);
        using var reader = new ContentSegmentReader(ContentPath);
        reader.ResolveContent(1).Should().Be(unicode);
    }

    [Fact]
    public void SegmentHeaderHasCorrectMagicAndCount()
    {
        var contents = new List<byte[]>
        {
            Encoding.UTF8.GetBytes("a"),
            Encoding.UTF8.GetBytes("b"),
            Encoding.UTF8.GetBytes("c"),
        };
        ContentSegmentWriter.Write(ContentPath, contents);

        // Read raw header bytes to verify
        var headerBytes = File.ReadAllBytes(ContentPath).AsSpan(0, 16);
        var magic = BitConverter.ToUInt32(headerBytes);
        var recordCount = BitConverter.ToUInt32(headerBytes[8..]);

        magic.Should().Be(StorageConstants.SegmentMagic);
        recordCount.Should().Be(3u);
    }
}
