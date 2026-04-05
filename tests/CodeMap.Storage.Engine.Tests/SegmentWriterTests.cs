namespace CodeMap.Storage.Engine.Tests;

using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

public sealed class SegmentWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-seg-test-{Guid.NewGuid():N}");

    public SegmentWriterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string SegPath(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void SymbolRecords_WriteAndReadBack()
    {
        var records = new SymbolRecord[]
        {
            new(1, 10, 20, 30, 40, 0, 1, 1, 1, 7, 0, 10, 50, 100, 42),
            new(2, 11, 21, 31, 41, 1, 2, 1, 8, 4, 1, 60, 120, 101, 43),
        };

        var path = SegPath("symbols.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<SymbolRecord>(path);
        reader.Count.Should().Be(2);
        reader[0].SymbolIntId.Should().Be(1);
        reader[0].Kind.Should().Be(1);
        reader[0].SignatureHash.Should().Be(42);
        reader[1].SymbolIntId.Should().Be(2);
        reader[1].Accessibility.Should().Be(4);
    }

    [Fact]
    public void FileRecords_WriteAndReadBack()
    {
        var records = new FileRecord[]
        {
            new(1, 5, 6, 1, 0x1234_5678_9ABC_DEF0, -1, 1, 0, 1),
            new(2, 7, 8, 2, 0, 0, 2, 1, 0),
        };

        var path = SegPath("files.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<FileRecord>(path);
        reader.Count.Should().Be(2);
        reader[0].FileIntId.Should().Be(1);
        reader[0].ContentHashHigh.Should().Be(0x1234_5678_9ABC_DEF0);
        reader[0].Language.Should().Be(1);
        reader[1].Language.Should().Be(2);
    }

    [Fact]
    public void ProjectRecords_WriteAndReadBack()
    {
        var records = new ProjectRecord[]
        {
            new(1, 10, 11, 12, 13, 0),
            new(2, 20, 21, 22, 23, 1),
        };

        var path = SegPath("projects.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<ProjectRecord>(path);
        reader.Count.Should().Be(2);
        reader[0].ProjectIntId.Should().Be(1);
        reader[0].NameStringId.Should().Be(10);
        reader[1].Flags.Should().Be(1);
    }

    [Fact]
    public void EdgeRecords_WriteAndReadBack()
    {
        var records = new EdgeRecord[]
        {
            new(1, 10, 20, 0, 1, 100, 200, 1, 0, 0, 1),
            new(2, 11, 0, 5, 2, 300, 400, 2, 1, 1, 0),
        };

        var path = SegPath("edges.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<EdgeRecord>(path);
        reader.Count.Should().Be(2);
        reader[0].FromSymbolIntId.Should().Be(10);
        reader[0].ToSymbolIntId.Should().Be(20);
        reader[1].ResolutionState.Should().Be(1);
        reader[1].ToNameStringId.Should().Be(5);
    }

    [Fact]
    public void FactRecords_WriteAndReadBack()
    {
        var records = new FactRecord[]
        {
            new(1, 5, 1, 10, 20, 0, 100, 200, 0, 0),
            new(2, 6, 2, 30, 40, 3, 101, 201, 1, 0),
        };

        var path = SegPath("facts.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<FactRecord>(path);
        reader.Count.Should().Be(2);
        reader[0].OwnerSymbolIntId.Should().Be(5);
        reader[0].FactKind.Should().Be(0);
        reader[1].FactKind.Should().Be(3);
        reader[1].Confidence.Should().Be(1);
    }

    [Fact]
    public void ZeroRecords_FileIsHeaderOnly()
    {
        var path = SegPath("empty.seg");
        SegmentWriter.Write<SymbolRecord>(path, ReadOnlySpan<SymbolRecord>.Empty);

        new FileInfo(path).Length.Should().Be(StorageConstants.SegFileHeaderSize);

        using var reader = new SegmentReader<SymbolRecord>(path);
        reader.Count.Should().Be(0);
    }

    [Fact]
    public void Header_HasCorrectMagicAndVersion()
    {
        var path = SegPath("header.seg");
        SegmentWriter.Write(path, new SymbolRecord[] { new(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) });

        var headerBytes = File.ReadAllBytes(path).AsSpan(0, 16);
        BitConverter.ToUInt32(headerBytes).Should().Be(StorageConstants.SegmentMagic);
        BitConverter.ToUInt16(headerBytes[4..]).Should().Be((ushort)StorageConstants.FormatMajor);
        BitConverter.ToUInt16(headerBytes[6..]).Should().Be((ushort)StorageConstants.FormatMinor);
        BitConverter.ToUInt32(headerBytes[8..]).Should().Be(1u);
    }

    [Fact]
    public void HundredRecords_AllFieldsPreserved()
    {
        var records = new SymbolRecord[100];
        for (var i = 0; i < 100; i++)
            records[i] = new(i + 1, i * 10, i * 20, i * 30, i * 40, 0, i + 1, 1, (short)(i % 15), 7, 0, i, i + 100, i * 50, i * 7);

        var path = SegPath("many.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<SymbolRecord>(path);
        reader.Count.Should().Be(100);

        for (var i = 0; i < 100; i++)
        {
            reader[i].SymbolIntId.Should().Be(i + 1);
            reader[i].StableIdStringId.Should().Be(i * 10);
            reader[i].FqnStringId.Should().Be(i * 20);
            reader[i].Kind.Should().Be((short)(i % 15));
            reader[i].SignatureHash.Should().Be(i * 7);
        }
    }

    [Fact]
    public void OutOfRangeIndex_Throws()
    {
        var path = SegPath("one.seg");
        SegmentWriter.Write(path, new SymbolRecord[] { new(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) });

        using var reader = new SegmentReader<SymbolRecord>(path);
        var act = () => reader[5];
        act.Should().Throw<StorageFormatException>();
    }

    [Fact]
    public void RecordsSpan_MatchesFullArray()
    {
        var records = new EdgeRecord[]
        {
            new(1, 10, 20, 0, 1, 0, 10, 1, 0, 0, 1),
            new(2, 11, 21, 0, 2, 0, 10, 1, 0, 0, 2),
            new(3, 12, 22, 0, 3, 0, 10, 1, 0, 0, 3),
        };

        var path = SegPath("edges2.seg");
        SegmentWriter.Write(path, records);

        using var reader = new SegmentReader<EdgeRecord>(path);
        var span = reader.Records;
        span.Length.Should().Be(3);
        span[2].Weight.Should().Be(3);
    }
}
