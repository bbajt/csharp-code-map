namespace CodeMap.Storage.Engine;

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Read-only access to content.seg (file body text) via memory-mapped file.
/// ContentId 0 returns empty string. Thread-safe after construction.
/// </summary>
internal sealed class ContentSegmentReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _count;
    private readonly int _offsetTableStart;
    private readonly int _dataBlobStart;
    private readonly unsafe byte* _basePtr;
    private bool _disposed;

    public ContentSegmentReader(string path)
    {
        var fileLength = new FileInfo(path).Length;
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        unsafe
        {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePtr = ptr;

            try
            {
                var header = MemoryMarshal.Read<SegmentFileHeader>(new ReadOnlySpan<byte>(ptr, StorageConstants.SegFileHeaderSize));

                if (header.Magic != StorageConstants.SegmentMagic)
                    throw new StorageFormatException($"Content segment magic mismatch: expected 0x{StorageConstants.SegmentMagic:X8}, got 0x{header.Magic:X8}");

                if (header.FormatMajor != StorageConstants.FormatMajor)
                    throw new StorageVersionException(header.FormatMajor, StorageConstants.FormatMajor);

                _count = (int)header.RecordCount;
                _offsetTableStart = StorageConstants.SegFileHeaderSize;
                _dataBlobStart = _offsetTableStart + (_count + 1) * sizeof(ulong); // uint64 offsets
            }
            catch
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _accessor.Dispose();
                _mmf.Dispose();
                throw;
            }
        }
    }

    public int Count => _count;

    /// <summary>
    /// Resolves a ContentId (1-based) to its UTF-8 string content.
    /// ContentId 0 returns empty string.
    /// </summary>
    public string ResolveContent(int contentId)
    {
        if (contentId == 0) return string.Empty;

        if (contentId < 0 || contentId > _count)
            throw new StorageFormatException($"ContentId {contentId} out of range [0..{_count}]");

        unsafe
        {
            var offsetTable = new ReadOnlySpan<ulong>(_basePtr + _offsetTableStart, _count + 1);
            var i = contentId - 1;
            var start = (long)offsetTable[i];
            var length = (int)(offsetTable[i + 1] - offsetTable[i]);
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(_basePtr + _dataBlobStart + start, length));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        unsafe
        {
            if (_basePtr != null)
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _accessor.Dispose();
        _mmf.Dispose();
    }
}
