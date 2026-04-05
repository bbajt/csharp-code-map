namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Fixed 20-byte header at the start of each page in WAL and overlay snapshot files.
/// Not used in v1 baseline .seg files (which are contiguous packed arrays per C-003 resolution).
/// See STORAGE-FORMAT.MD §3.2.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct PageHeader
{
    /// <summary>Must equal <see cref="StorageConstants.PageMagic"/> (0xC04E_4D50).</summary>
    public readonly uint Magic;

    /// <summary>Must match reader. Currently <see cref="StorageConstants.FormatMajor"/> = 2.</summary>
    public readonly ushort FormatVersion;

    /// <summary>Page type constant. See STORAGE-FORMAT.MD §3.3 for the full enum.</summary>
    public readonly ushort PageType;

    /// <summary>0-based index within the owning segment.</summary>
    public readonly uint PageNumber;

    /// <summary>Bytes of live data after this header. Max = PageSize - PageHeaderSize = 8172.</summary>
    public readonly ushort PayloadBytes;

    /// <summary>Must be zero.</summary>
    public readonly ushort Reserved;

    /// <summary>CRC32 of bytes [0..15] with this field set to 0 during computation.</summary>
    public readonly uint Crc32;
    // sizeof = 20
}
