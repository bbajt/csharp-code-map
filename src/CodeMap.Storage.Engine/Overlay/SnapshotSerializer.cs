namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Serializes/deserializes overlay in-memory state to/from a binary snapshot file.
/// Simple sequential format — not paged. Typically < 10 MB even for large workspaces.
/// </summary>
internal static class SnapshotSerializer
{
    private const uint SnapshotMagic = 0x434D_534E; // 'CMSN'
    private const int Version = 2; // v2: added NextOverlayFileIntId + NextOverlayFactIntId

    public static void Write(string path, EngineOverlay overlay)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);

        bw.Write(SnapshotMagic);
        bw.Write(Version);
        bw.Write(overlay.Revision);
        bw.Write(overlay.NextOverlayStringId);
        bw.Write(overlay.NextOverlaySymbolIntId);
        bw.Write(overlay.NextOverlayEdgeIntId);
        bw.Write(overlay.NextOverlayFileIntId);
        bw.Write(overlay.NextOverlayFactIntId);

        // Symbols
        bw.Write(overlay.SymbolsByStableId.Count);
        foreach (var (stableId, sym) in overlay.SymbolsByStableId)
        {
            WriteString(bw, stableId);
            WriteStruct(bw, sym);
        }

        // Tombstones
        bw.Write(overlay.TombstoneSet.Count);
        foreach (var ts in overlay.TombstoneSet)
            WriteString(bw, ts);

        // Edges: collect all unique edges from both outgoing and incoming maps.
        // ApplyEdge on read rebuilds both directions, so we just need each edge once.
        var allEdges = new HashSet<(int, int, int, int)>(); // (EdgeIntId, From, To, FileIntId) as dedup key
        var edgeList = new List<EdgeRecord>();
        foreach (var list in overlay.OutgoingEdges.Values)
            foreach (var edge in list)
                if (allEdges.Add((edge.EdgeIntId, edge.FromSymbolIntId, edge.ToSymbolIntId, edge.FileIntId)))
                    edgeList.Add(edge);
        foreach (var list in overlay.IncomingEdges.Values)
            foreach (var edge in list)
                if (allEdges.Add((edge.EdgeIntId, edge.FromSymbolIntId, edge.ToSymbolIntId, edge.FileIntId)))
                    edgeList.Add(edge);

        bw.Write(edgeList.Count);
        foreach (var edge in edgeList)
            WriteStruct(bw, edge);

        // Facts
        var factCount = overlay.FactsBySymbol.Values.Sum(l => l.Count);
        bw.Write(factCount);
        foreach (var list in overlay.FactsBySymbol.Values)
            foreach (var fact in list)
                WriteStruct(bw, fact);

        // Files
        bw.Write(overlay.FilesByPath.Count);
        foreach (var (filePath, file) in overlay.FilesByPath)
        {
            WriteString(bw, filePath);
            WriteStruct(bw, file);
        }

        // Overlay dictionary
        bw.Write(overlay.OverlayDictionary.Count);
        foreach (var (id, value) in overlay.OverlayDictionary)
        {
            bw.Write(id);
            WriteString(bw, value);
        }

        // Token map
        bw.Write(overlay.TokenMap.Count);
        foreach (var (token, ids) in overlay.TokenMap)
        {
            WriteString(bw, token);
            bw.Write(ids.Count);
            foreach (var id in ids)
                bw.Write(id);
        }

        bw.Flush();
    }

    public static void Read(string path, EngineOverlay overlay)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        var magic = br.ReadUInt32();
        if (magic != SnapshotMagic) throw new StorageFormatException($"Snapshot magic mismatch: 0x{magic:X8}");

        var version = br.ReadInt32();
        if (version != Version) throw new StorageVersionException(version, Version);

        overlay.Revision = br.ReadInt32();
        overlay.NextOverlayStringId = br.ReadInt32();
        overlay.NextOverlaySymbolIntId = br.ReadInt32();
        overlay.NextOverlayEdgeIntId = br.ReadInt32();
        overlay.NextOverlayFileIntId = br.ReadInt32();
        overlay.NextOverlayFactIntId = br.ReadInt32();

        // Symbols
        var symCount = br.ReadInt32();
        for (var i = 0; i < symCount; i++)
        {
            var stableId = ReadString(br);
            var sym = ReadStruct<SymbolRecord>(br);
            overlay.SymbolsByStableId[stableId] = sym;
        }

        // Tombstones
        var tsCount = br.ReadInt32();
        for (var i = 0; i < tsCount; i++)
            overlay.TombstoneSet.Add(ReadString(br));

        // Edges
        var edgeCount = br.ReadInt32();
        for (var i = 0; i < edgeCount; i++)
        {
            var edge = ReadStruct<EdgeRecord>(br);
            overlay.ApplyEdge(edge);
        }

        // Facts
        var factCount = br.ReadInt32();
        for (var i = 0; i < factCount; i++)
        {
            var fact = ReadStruct<FactRecord>(br);
            overlay.ApplyFact(fact);
        }

        // Files
        var fileCount = br.ReadInt32();
        for (var i = 0; i < fileCount; i++)
        {
            var filePath = ReadString(br);
            var file = ReadStruct<FileRecord>(br);
            overlay.FilesByPath[filePath] = file;
        }

        // Overlay dictionary
        var dictCount = br.ReadInt32();
        for (var i = 0; i < dictCount; i++)
        {
            var id = br.ReadInt32();
            var value = ReadString(br);
            overlay.ApplyDictionaryEntry(id, value);
        }

        // Token map
        var tokenCount = br.ReadInt32();
        for (var i = 0; i < tokenCount; i++)
        {
            var token = ReadString(br);
            var count = br.ReadInt32();
            var ids = new HashSet<int>(count);
            for (var j = 0; j < count; j++)
                ids.Add(br.ReadInt32());
            overlay.TokenMap[token] = ids;
        }
    }

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        var len = br.ReadInt32();
        var bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteStruct<T>(BinaryWriter bw, in T value) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        Span<byte> buf = stackalloc byte[size];
        MemoryMarshal.Write(buf, in value);
        bw.Write(buf);
    }

    private static T ReadStruct<T>(BinaryReader br) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var bytes = br.ReadBytes(size);
        return MemoryMarshal.Read<T>(bytes);
    }
}
