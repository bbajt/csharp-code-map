namespace CodeMap.Storage.Engine;

using System.Collections.Frozen;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Read-only string resolution over an mmap'd dictionary.seg file.
/// Thread-safe after construction (immutable data).
/// Dispose to release the memory-mapped file handle.
/// </summary>
internal sealed class DictionaryReader : IDictionaryReader, IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _count;
    private readonly int _offsetTableStart;  // byte offset in mmap where offset table begins
    private readonly int _dataBlobStart;     // byte offset in mmap where data blob begins
    private readonly unsafe byte* _basePtr;
    private readonly FrozenDictionary<string, int> _reverseIndex;
    private bool _disposed;

    public DictionaryReader(string path)
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
                    throw new StorageFormatException($"Dictionary segment magic mismatch: expected 0x{StorageConstants.SegmentMagic:X8}, got 0x{header.Magic:X8}");

                if (header.FormatMajor != StorageConstants.FormatMajor)
                    throw new StorageVersionException(header.FormatMajor, StorageConstants.FormatMajor);

                _count = (int)header.RecordCount;
                _offsetTableStart = StorageConstants.SegFileHeaderSize;
                _dataBlobStart = _offsetTableStart + (_count + 1) * sizeof(uint);
            }
            catch
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _accessor.Dispose();
                _mmf.Dispose();
                throw;
            }
        }

        // Build reverse lookup index (C-020: FrozenDictionary for O(1) TryFind)
        var builder = new Dictionary<string, int>(_count, StringComparer.Ordinal);
        for (var i = 1; i <= _count; i++)
            builder[Resolve(i)] = i;
        _reverseIndex = builder.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public string Resolve(int stringId)
    {
        if (stringId == 0) return string.Empty;
        var utf8 = ResolveUtf8(stringId);
        return Encoding.UTF8.GetString(utf8);
    }

    /// <inheritdoc />
    public ReadOnlySpan<byte> ResolveUtf8(int stringId)
    {
        if (stringId == 0) return ReadOnlySpan<byte>.Empty;

        if (stringId < 0 || stringId > _count)
            throw new StorageFormatException($"StringId {stringId} out of range [0..{_count}]");

        unsafe
        {
            var offsetTable = new ReadOnlySpan<uint>(_basePtr + _offsetTableStart, _count + 1);
            var i = stringId - 1;
            var start = (int)offsetTable[i];
            var length = (int)(offsetTable[i + 1] - offsetTable[i]);
            return new ReadOnlySpan<byte>(_basePtr + _dataBlobStart + start, length);
        }
    }

    /// <inheritdoc />
    public bool TryFind(string value, out int stringId)
        => _reverseIndex.TryGetValue(value, out stringId);

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
