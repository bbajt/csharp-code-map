namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Fixed 16-byte header at the start of every .seg and .idx file.
/// See STORAGE-FORMAT.MD §2.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct SegmentFileHeader
{
    /// <summary>Must equal <see cref="StorageConstants.SegmentMagic"/> (0x434D_7632 = 'CMv2').</summary>
    public readonly uint Magic;

    /// <summary>Must match reader. Currently <see cref="StorageConstants.FormatMajor"/> = 2.</summary>
    public readonly ushort FormatMajor;

    /// <summary>Reader accepts any minor >= own. Currently <see cref="StorageConstants.FormatMinor"/> = 0.</summary>
    public readonly ushort FormatMinor;

    /// <summary>Number of records / entries in this segment.</summary>
    public readonly uint RecordCount;

    /// <summary>Must be zero.</summary>
    public readonly uint Reserved;
    // sizeof = 16
}
