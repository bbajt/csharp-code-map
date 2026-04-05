namespace CodeMap.Storage.Engine;

using System.Text;

/// <summary>
/// Writes file body text to content.seg per STORAGE-FORMAT.MD §4A.
/// Layout: [SegmentFileHeader][OffsetTable: uint64 × (Count+1)][ContentBlob: packed UTF-8].
/// ContentIds are 1-based; ContentId 0 = no content.
/// </summary>
internal static class ContentSegmentWriter
{
    /// <summary>
    /// Writes content entries to the specified path. Entries must be in ContentId order (1-based).
    /// </summary>
    public static void Write(string path, IReadOnlyList<byte[]> utf8Contents)
    {
        var count = utf8Contents.Count;

        // Compute uint64 offset table
        var offsets = new ulong[count + 1];
        ulong running = 0;
        for (var i = 0; i < count; i++)
        {
            offsets[i] = running;
            running += (ulong)utf8Contents[i].Length;
        }
        offsets[count] = running; // sentinel

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        // SegmentFileHeader (16 bytes)
        bw.Write(StorageConstants.SegmentMagic);
        bw.Write((ushort)StorageConstants.FormatMajor);
        bw.Write((ushort)StorageConstants.FormatMinor);
        bw.Write((uint)count);
        bw.Write(0u); // Reserved

        // OffsetTable (uint64 × (count + 1))
        foreach (var offset in offsets)
            bw.Write(offset);

        // ContentBlob
        foreach (var content in utf8Contents)
            bw.Write(content);

        bw.Flush();
        fs.Flush(true);
    }
}
