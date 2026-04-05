namespace CodeMap.Storage.Engine.Tests;

using System.IO.Hashing;
using FluentAssertions;
using Xunit;

public sealed class ChecksumWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-checksum-test-{Guid.NewGuid():N}");

    public ChecksumWriterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ComputeCrc32Hex_MatchesSystemHash()
    {
        var filePath = Path.Combine(_tempDir, "test.bin");
        var data = "Hello, CRC32!"u8.ToArray();
        File.WriteAllBytes(filePath, data);

        var expected = Convert.ToHexString(Crc32.Hash(data));
        ChecksumWriter.ComputeCrc32Hex(filePath).Should().Be(expected);
    }

    [Fact]
    public void ComputeSha1_Returns20Bytes()
    {
        var filePath = Path.Combine(_tempDir, "test.bin");
        File.WriteAllBytes(filePath, [1, 2, 3, 4, 5]);

        var sha1 = ChecksumWriter.ComputeSha1(filePath);
        sha1.Length.Should().Be(20);
    }

    [Fact]
    public void WriteChecksums_ReturnsCrcMap()
    {
        // Create two dummy segment files
        var seg1 = Path.Combine(_tempDir, "symbols.seg");
        var seg2 = Path.Combine(_tempDir, "edges.seg");
        File.WriteAllBytes(seg1, [0xDE, 0xAD, 0xBE, 0xEF]);
        File.WriteAllBytes(seg2, [0xCA, 0xFE, 0xBA, 0xBE]);

        var checksumsPath = Path.Combine(_tempDir, "checksums.bin");
        var segments = new List<(string, string)>
        {
            ("symbols", seg1),
            ("edges", seg2),
        };

        var crcMap = ChecksumWriter.WriteChecksums(checksumsPath, segments);

        crcMap.Should().ContainKey("symbols");
        crcMap.Should().ContainKey("edges");
        crcMap["symbols"].Should().Be(Convert.ToHexString(Crc32.Hash(File.ReadAllBytes(seg1))));

        File.Exists(checksumsPath).Should().BeTrue();
        new FileInfo(checksumsPath).Length.Should().BeGreaterThan(16);
    }
}
