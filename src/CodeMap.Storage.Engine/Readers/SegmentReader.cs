namespace CodeMap.Storage.Engine;

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

/// <summary>
/// Read-only access to a contiguous-record .seg file via memory-mapped I/O.
/// Records of type T are accessed via zero-copy span reads.
/// </summary>
internal sealed class SegmentReader<T> : IDisposable where T : unmanaged
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _count;
    private readonly unsafe byte* _basePtr;
    private bool _disposed;

    public SegmentReader(string path)
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
                    throw new StorageFormatException($"Segment magic mismatch: expected 0x{StorageConstants.SegmentMagic:X8}, got 0x{header.Magic:X8}");

                if (header.FormatMajor != StorageConstants.FormatMajor)
                    throw new StorageVersionException(header.FormatMajor, StorageConstants.FormatMajor);

                _count = (int)header.RecordCount;
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

    /// <summary>Returns a zero-copy span over all records in the segment.</summary>
    public ReadOnlySpan<T> Records
    {
        get
        {
            unsafe
            {
                return MemoryMarshal.Cast<byte, T>(
                    new ReadOnlySpan<byte>(_basePtr + StorageConstants.SegFileHeaderSize, _count * Marshal.SizeOf<T>()));
            }
        }
    }

    /// <summary>Returns a single record by 0-based index.</summary>
    public ref readonly T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new StorageFormatException($"Record index {index} out of range [0..{_count})");
            return ref Records[index];
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
