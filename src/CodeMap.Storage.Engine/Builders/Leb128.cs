namespace CodeMap.Storage.Engine;

/// <summary>
/// LEB128 unsigned variable-length integer encoding.
/// Used for delta-encoded postings in search.idx.
/// </summary>
internal static class Leb128
{
    public static void Write(Stream stream, uint value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            stream.WriteByte(b);
        } while (value != 0);
    }

    public static void Write(Span<byte> buffer, uint value, out int bytesWritten)
    {
        var pos = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            buffer[pos++] = b;
        } while (value != 0);
        bytesWritten = pos;
    }

    public static uint Read(ReadOnlySpan<byte> data, ref int offset)
    {
        uint result = 0;
        var shift = 0;
        byte b;
        do
        {
            if (offset >= data.Length)
                throw new StorageFormatException($"LEB128 read past end of data at offset {offset}");
            b = data[offset++];
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }
}
