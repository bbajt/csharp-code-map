namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Writes contiguous packed record arrays to .seg files per STORAGE-FORMAT.MD §3.0.
/// Layout: [SegmentFileHeader 16B][records: T × count]. No page headers.
/// </summary>
internal static class SegmentWriter
{
    public static void Write<T>(string path, T[] records) where T : unmanaged
        => Write(path, (ReadOnlySpan<T>)records);

    public static void Write<T>(string path, ReadOnlySpan<T> records) where T : unmanaged
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        // SegmentFileHeader (16 bytes)
        Span<byte> header = stackalloc byte[StorageConstants.SegFileHeaderSize];
        BitConverter.TryWriteBytes(header, StorageConstants.SegmentMagic);
        BitConverter.TryWriteBytes(header[4..], (ushort)StorageConstants.FormatMajor);
        BitConverter.TryWriteBytes(header[6..], (ushort)StorageConstants.FormatMinor);
        BitConverter.TryWriteBytes(header[8..], (uint)records.Length);
        BitConverter.TryWriteBytes(header[12..], 0u); // Reserved
        fs.Write(header);

        // Records — contiguous packed array
        fs.Write(MemoryMarshal.AsBytes(records));

        fs.Flush(true);
    }
}
