namespace CodeMap.Storage.Engine;

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

/// <summary>
/// Reads adjacency-out.idx / adjacency-in.idx via memory-mapped I/O.
/// Returns sorted EdgeIntId arrays per symbol. Thread-safe (immutable data).
/// </summary>
internal sealed class AdjacencyIndexReader : IEngineAdjacencyIndex, IDisposable
{
    private readonly MemoryMappedFile _outMmf;
    private readonly MemoryMappedViewAccessor _outAccessor;
    private readonly MemoryMappedFile _inMmf;
    private readonly MemoryMappedViewAccessor _inAccessor;
    private readonly unsafe byte* _outPtr;
    private readonly unsafe byte* _inPtr;
    private readonly int _maxSymbolId;
    private bool _disposed;

    public AdjacencyIndexReader(string outPath, string inPath, int maxSymbolId)
    {
        _maxSymbolId = maxSymbolId;

        var outLen = new FileInfo(outPath).Length;
        _outMmf = MemoryMappedFile.CreateFromFile(outPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _outAccessor = _outMmf.CreateViewAccessor(0, outLen, MemoryMappedFileAccess.Read);

        var inLen = new FileInfo(inPath).Length;
        _inMmf = MemoryMappedFile.CreateFromFile(inPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _inAccessor = _inMmf.CreateViewAccessor(0, inLen, MemoryMappedFileAccess.Read);

        unsafe
        {
            byte* p = null;
            _outAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _outPtr = p;

            try
            {
                p = null;
                _inAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _inPtr = p;
            }
            catch
            {
                if (_inPtr != null)
                    _inAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _outAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _inAccessor.Dispose();
                _inMmf.Dispose();
                _outAccessor.Dispose();
                _outMmf.Dispose();
                throw;
            }
        }
    }

    public unsafe ReadOnlySpan<int> GetOutgoingEdgeIds(int symbolIntId)
        => GetEdgeIds(_outPtr, symbolIntId);

    public unsafe ReadOnlySpan<int> GetIncomingEdgeIds(int symbolIntId)
        => GetEdgeIds(_inPtr, symbolIntId);

    public bool HasEdge(int fromSymbolIntId, int toSymbolIntId)
    {
        // Not directly supported by adjacency index (it stores EdgeIntIds, not ToSymbolIntIds).
        // Caller must resolve edge records. Return false as placeholder.
        // This will be implemented properly in T05 when we have edge record access.
        return false;
    }

    private unsafe ReadOnlySpan<int> GetEdgeIds(byte* basePtr, int symbolIntId)
    {
        if (symbolIntId < 1 || symbolIntId > _maxSymbolId)
            return [];

        var headerTableStart = StorageConstants.SegFileHeaderSize;
        var headerTable = new ReadOnlySpan<uint>(basePtr + headerTableStart, _maxSymbolId + 2);

        var blockOffset = headerTable[symbolIntId];
        if (blockOffset == 0) return [];

        var postingsBase = headerTableStart + (_maxSymbolId + 2) * sizeof(uint);
        var pos = postingsBase + (int)(blockOffset - 1); // 1-based offset

        var count = MemoryMarshal.Read<int>(new ReadOnlySpan<byte>(basePtr + pos, 4));
        pos += 4;

        return MemoryMarshal.Cast<byte, int>(new ReadOnlySpan<byte>(basePtr + pos, count * 4));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        unsafe
        {
            if (_outPtr != null)
                _outAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            if (_inPtr != null)
                _inAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _outAccessor.Dispose();
        _outMmf.Dispose();
        _inAccessor.Dispose();
        _inMmf.Dispose();
    }
}
