namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Fixed 20-byte header preceding every WAL record payload.
/// See STORAGE-FORMAT.MD §13.1.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct WalRecordHeader
{
    /// <summary>Must equal <see cref="StorageConstants.WalMagic"/> (0xC04E_574C).</summary>
    public readonly uint Magic;

    /// <summary>Must match reader. Currently <see cref="StorageConstants.FormatMajor"/> = 2.</summary>
    public readonly ushort FormatVersion;

    /// <summary>WAL record type: 0x01=AddSymbol through 0x0A=CheckpointComplete. See STORAGE-FORMAT.MD §13.2.</summary>
    public readonly ushort RecordType;

    /// <summary>Monotonically increasing from 1 within a single WAL file.</summary>
    public readonly uint SequenceNumber;

    /// <summary>Bytes of payload following this header.</summary>
    public readonly uint PayloadBytes;

    /// <summary>CRC32 of [0..15] + payload, with this field set to 0 during computation.</summary>
    public readonly uint Crc32;
    // sizeof = 20
}
