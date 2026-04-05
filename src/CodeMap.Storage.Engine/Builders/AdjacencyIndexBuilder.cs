namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Builds adjacency-out.idx and adjacency-in.idx per STORAGE-FORMAT.MD §11.
/// Layout: [SegmentFileHeader][HeaderTable: uint32 × (MaxSymbolId+2)][PostingsRegion].
/// HeaderTable[i] = byte offset into PostingsRegion for symbol i (0 = no edges).
/// Each postings block: [uint32 Count][int32 × Count: sorted EdgeIntIds].
/// </summary>
internal static class AdjacencyIndexBuilder
{
    /// <summary>
    /// Builds both adjacency index files from the edge records.
    /// </summary>
    public static void Build(string outPath, string inPath, ReadOnlySpan<EdgeRecord> edges, int maxSymbolId)
    {
        // Collect outgoing (From → EdgeIntId) and incoming (To → EdgeIntId)
        var outgoing = new Dictionary<int, List<int>>();
        var incoming = new Dictionary<int, List<int>>();

        foreach (ref readonly var edge in edges)
        {
            if (edge.FromSymbolIntId > 0)
            {
                if (!outgoing.TryGetValue(edge.FromSymbolIntId, out var outList))
                {
                    outList = [];
                    outgoing[edge.FromSymbolIntId] = outList;
                }
                outList.Add(edge.EdgeIntId);
            }

            if (edge.ToSymbolIntId > 0)
            {
                if (!incoming.TryGetValue(edge.ToSymbolIntId, out var inList))
                {
                    inList = [];
                    incoming[edge.ToSymbolIntId] = inList;
                }
                inList.Add(edge.EdgeIntId);
            }
        }

        WriteIndex(outPath, outgoing, maxSymbolId);
        WriteIndex(inPath, incoming, maxSymbolId);
    }

    /// <summary>Array overload for convenience.</summary>
    public static void Build(string outPath, string inPath, EdgeRecord[] edges, int maxSymbolId)
        => Build(outPath, inPath, (ReadOnlySpan<EdgeRecord>)edges, maxSymbolId);

    private static void WriteIndex(string path, Dictionary<int, List<int>> adjacency, int maxSymbolId)
    {
        // Sort each posting list
        foreach (var list in adjacency.Values)
            list.Sort();

        // Build postings region into memory
        using var postingsStream = new MemoryStream();
        var headerTable = new uint[maxSymbolId + 2]; // indices [0..maxSymbolId+1]
        var intBuf = new byte[4]; // reusable buffer for int32 writes

        for (var symbolId = 1; symbolId <= maxSymbolId; symbolId++)
        {
            if (!adjacency.TryGetValue(symbolId, out var edgeIds))
            {
                headerTable[symbolId] = 0; // no edges
                continue;
            }

            // Block offset relative to PostingsRegion start
            // +1 because offset 0 means "no edges", so we use 1-based offsets
            headerTable[symbolId] = (uint)postingsStream.Position + 1;

            // Write [Count][EdgeIntIds...]
            BitConverter.TryWriteBytes(intBuf, edgeIds.Count);
            postingsStream.Write(intBuf);

            foreach (var edgeId in edgeIds)
            {
                BitConverter.TryWriteBytes(intBuf, edgeId);
                postingsStream.Write(intBuf);
            }
        }

        // Sentinel: total postings region size + 1
        headerTable[maxSymbolId + 1] = (uint)postingsStream.Position + 1;

        var postingsBytes = postingsStream.ToArray();

        // Count symbols with edges for RecordCount (exclude sentinel at maxSymbolId+1)
        var recordCount = 0;
        for (var symbolId = 1; symbolId <= maxSymbolId; symbolId++)
        {
            if (headerTable[symbolId] != 0) recordCount++;
        }

        // Write file
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        // SegmentFileHeader
        Span<byte> header = stackalloc byte[StorageConstants.SegFileHeaderSize];
        BitConverter.TryWriteBytes(header, StorageConstants.SegmentMagic);
        BitConverter.TryWriteBytes(header[4..], (ushort)StorageConstants.FormatMajor);
        BitConverter.TryWriteBytes(header[6..], (ushort)StorageConstants.FormatMinor);
        BitConverter.TryWriteBytes(header[8..], (uint)recordCount);
        BitConverter.TryWriteBytes(header[12..], 0u);
        fs.Write(header);

        // HeaderTable
        fs.Write(MemoryMarshal.AsBytes(headerTable.AsSpan()));

        // PostingsRegion
        fs.Write(postingsBytes);
        fs.Flush(true);
    }

    /// <summary>
    /// Reads edge IDs for a given symbol from an adjacency index file.
    /// Returns empty array if symbol has no edges.
    /// </summary>
    public static int[] ReadEdgeIds(byte[] fileBytes, int symbolIntId, int maxSymbolId)
    {
        var headerTableStart = StorageConstants.SegFileHeaderSize;
        var headerTable = MemoryMarshal.Cast<byte, uint>(
            fileBytes.AsSpan(headerTableStart, (maxSymbolId + 2) * sizeof(uint)));

        var blockOffset = headerTable[symbolIntId];
        if (blockOffset == 0) return [];

        var postingsBase = headerTableStart + (maxSymbolId + 2) * sizeof(uint);
        var pos = postingsBase + (int)(blockOffset - 1); // -1 because 1-based offset

        var count = BitConverter.ToInt32(fileBytes.AsSpan(pos));
        pos += 4;

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToInt32(fileBytes.AsSpan(pos));
            pos += 4;
        }
        return result;
    }
}
