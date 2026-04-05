namespace CodeMap.Storage.Engine;

using System.IO.Hashing;
using System.Security.Cryptography;

/// <summary>
/// Computes CRC32 + SHA-1 for each segment file and writes checksums.bin.
/// Per STORAGE-FORMAT.MD §15.
/// </summary>
internal static class ChecksumWriter
{
    /// <summary>
    /// Computes CRC32 of an entire file. Returns uppercase hex string.
    /// </summary>
    public static string ComputeCrc32Hex(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = Crc32.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes SHA-1 of an entire file. Returns the 20-byte hash.
    /// </summary>
    public static byte[] ComputeSha1(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return SHA1.HashData(bytes);
    }

    /// <summary>
    /// Writes checksums.bin containing CRC32 + SHA-1 for each segment file.
    /// Returns a dictionary of segment name → CRC32 hex string (for manifest).
    /// </summary>
    public static Dictionary<string, string> WriteChecksums(
        string checksumsPath,
        IReadOnlyList<(string SegmentName, string FilePath)> segments)
    {
        var crcMap = new Dictionary<string, string>(StringComparer.Ordinal);

        using var fs = new FileStream(checksumsPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // SegmentFileHeader
        bw.Write(StorageConstants.SegmentMagic);
        bw.Write((ushort)StorageConstants.FormatMajor);
        bw.Write((ushort)StorageConstants.FormatMinor);
        bw.Write((uint)segments.Count);
        bw.Write(0u); // Reserved

        // Collect segment names for mini-dict
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<byte[]>();
        foreach (var (name, _) in segments)
        {
            if (!nameToId.ContainsKey(name))
            {
                nameToId[name] = nameToId.Count + 1;
                names.Add(System.Text.Encoding.UTF8.GetBytes(name));
            }
        }

        // Write SegmentChecksum records (32 bytes each)
        foreach (var (segmentName, filePath) in segments)
        {
            var fileBytes = File.ReadAllBytes(filePath);
            var crc32Bytes = Crc32.Hash(fileBytes);
            var sha1Bytes = SHA1.HashData(fileBytes);
            var crcHex = Convert.ToHexString(crc32Bytes);
            crcMap[segmentName] = crcHex;

            bw.Write(nameToId[segmentName]); // NameStringId (4 bytes)
            bw.Write((uint)fileBytes.Length); // FileSizeBytes (4 bytes)
            bw.Write(BitConverter.ToUInt32(crc32Bytes)); // Crc32 (4 bytes)
            bw.Write(sha1Bytes); // SHA-1 (20 bytes)
        }

        // Mini-dict: [count:uint32][offset table: uint32 × (count+1)][data blob]
        bw.Write((uint)names.Count);
        uint offset = 0;
        foreach (var n in names)
        {
            bw.Write(offset);
            offset += (uint)n.Length;
        }
        bw.Write(offset); // sentinel

        foreach (var n in names)
            bw.Write(n);

        bw.Flush();
        fs.Flush(true);

        return crcMap;
    }
}
