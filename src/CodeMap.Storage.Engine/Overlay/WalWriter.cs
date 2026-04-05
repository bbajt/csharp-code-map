namespace CodeMap.Storage.Engine;

using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Writes WAL records per STORAGE-FORMAT.MD §13. Each record = 20-byte header + payload.
/// CRC32 covers header (with CRC field zeroed) + payload.
/// </summary>
internal sealed class WalWriter : IDisposable
{
    private readonly FileStream _stream;
    private uint _sequenceNumber;

    public WalWriter(string walPath, uint startSequence = 0)
    {
        _stream = new FileStream(walPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End); // append
        _sequenceNumber = startSequence;
    }

    public uint LastSequence => _sequenceNumber;

    public void WriteRecord(ushort recordType, ReadOnlySpan<byte> payload)
    {
        _sequenceNumber++;
        var headerSize = Marshal.SizeOf<WalRecordHeader>(); // 20
        Span<byte> headerBytes = stackalloc byte[headerSize];

        // Build header with CRC=0
        BitConverter.TryWriteBytes(headerBytes, StorageConstants.WalMagic);
        BitConverter.TryWriteBytes(headerBytes[4..], (ushort)StorageConstants.FormatMajor);
        BitConverter.TryWriteBytes(headerBytes[6..], recordType);
        BitConverter.TryWriteBytes(headerBytes[8..], _sequenceNumber);
        BitConverter.TryWriteBytes(headerBytes[12..], (uint)payload.Length);
        BitConverter.TryWriteBytes(headerBytes[16..], 0u); // CRC placeholder

        // Compute CRC32 over header (CRC=0) + payload
        var crc = new Crc32();
        crc.Append(headerBytes);
        crc.Append(payload);
        var crcValue = BitConverter.ToUInt32(crc.GetCurrentHash());

        // Write CRC into header
        BitConverter.TryWriteBytes(headerBytes[16..], crcValue);

        _stream.Write(headerBytes);
        _stream.Write(payload);
    }

    public void WriteSymbolRecord(ushort recordType, in SymbolRecord record)
    {
        Span<byte> payload = stackalloc byte[Marshal.SizeOf<SymbolRecord>()];
        MemoryMarshal.Write(payload, in record);
        WriteRecord(recordType, payload);
    }

    public void WriteEdgeRecord(ushort recordType, in EdgeRecord record)
    {
        Span<byte> payload = stackalloc byte[Marshal.SizeOf<EdgeRecord>()];
        MemoryMarshal.Write(payload, in record);
        WriteRecord(recordType, payload);
    }

    public void WriteFactRecord(in FactRecord record)
    {
        Span<byte> payload = stackalloc byte[Marshal.SizeOf<FactRecord>()];
        MemoryMarshal.Write(payload, in record);
        WriteRecord(0x05, payload); // AddFact
    }

    public void WriteFileRecord(in FileRecord record)
    {
        Span<byte> payload = stackalloc byte[Marshal.SizeOf<FileRecord>()];
        MemoryMarshal.Write(payload, in record);
        WriteRecord(0x06, payload); // AddFile
    }

    public void WriteTombstone(int entityKind, int entityIntId, int stableIdStringId, int flags)
    {
        Span<byte> payload = stackalloc byte[16]; // TombstoneRecord = 16 bytes
        BitConverter.TryWriteBytes(payload, entityKind);
        BitConverter.TryWriteBytes(payload[4..], entityIntId);
        BitConverter.TryWriteBytes(payload[8..], stableIdStringId);
        BitConverter.TryWriteBytes(payload[12..], flags);
        WriteRecord(0x07, payload);
    }

    public void WriteDictionaryAdd(int stringId, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        // Payload: uint32 StringId + varint byteLen + UTF-8 bytes
        using var ms = new MemoryStream();
        var idBytes = new byte[4];
        BitConverter.TryWriteBytes(idBytes, stringId);
        ms.Write(idBytes);
        Leb128.Write(ms, (uint)utf8.Length);
        ms.Write(utf8);
        var payload = ms.ToArray();
        WriteRecord(0x08, payload);
    }

    public void Flush(bool flushToDisk = true)
    {
        _stream.Flush(flushToDisk);
    }

    public long Length => _stream.Length;
    public long Position => _stream.Position;

    public void Dispose() => _stream.Dispose();
}
