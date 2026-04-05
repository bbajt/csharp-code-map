namespace CodeMap.Storage.Engine;

using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Reads WAL records for recovery. Stops at first CRC mismatch or incomplete record.
/// Returns the sequence number of the last valid record.
/// </summary>
internal static class WalReader
{
    /// <summary>
    /// Replays valid WAL records, invoking the callback for each.
    /// Returns the sequence number of the last successfully replayed record.
    /// </summary>
    public static uint Replay(string walPath, uint afterSequence, Action<ushort, uint, byte[]> onRecord)
    {
        if (!File.Exists(walPath)) return afterSequence;

        var fileBytes = File.ReadAllBytes(walPath);
        if (fileBytes.Length == 0) return afterSequence;

        var headerSize = Marshal.SizeOf<WalRecordHeader>(); // 20
        var pos = 0;
        var lastValid = afterSequence;

        while (pos + headerSize <= fileBytes.Length)
        {
            var headerSpan = fileBytes.AsSpan(pos, headerSize);

            // Read header fields
            var magic = BitConverter.ToUInt32(headerSpan);
            if (magic != StorageConstants.WalMagic) break; // Bad magic → stop

            var recordType = BitConverter.ToUInt16(headerSpan[6..]);
            var seqNum = BitConverter.ToUInt32(headerSpan[8..]);
            var payloadBytes = BitConverter.ToUInt32(headerSpan[12..]);
            var storedCrc = BitConverter.ToUInt32(headerSpan[16..]);

            // Check we have enough data for payload
            if (pos + headerSize + (int)payloadBytes > fileBytes.Length) break; // Incomplete → stop

            var payloadSpan = fileBytes.AsSpan(pos + headerSize, (int)payloadBytes);

            // Verify CRC32: header with CRC zeroed + payload
            var headerForCrc = new byte[headerSize];
            headerSpan.CopyTo(headerForCrc);
            BitConverter.TryWriteBytes(headerForCrc.AsSpan(16), 0u); // Zero CRC field

            var crc = new Crc32();
            crc.Append(headerForCrc);
            crc.Append(payloadSpan);
            var computedCrc = BitConverter.ToUInt32(crc.GetCurrentHash());

            if (computedCrc != storedCrc) break; // CRC mismatch → stop

            // Record is valid — replay if after the checkpoint sequence
            if (seqNum > afterSequence)
            {
                onRecord(recordType, seqNum, payloadSpan.ToArray());
                lastValid = seqNum;
            }

            pos += headerSize + (int)payloadBytes;
        }

        return lastValid;
    }

    /// <summary>Parses a DictionaryAdd payload into (StringId, string value).</summary>
    public static (int StringId, string Value) ParseDictionaryAdd(byte[] payload)
    {
        var stringId = BitConverter.ToInt32(payload);
        var offset = 4;
        var byteLen = (int)Leb128.Read(payload, ref offset);
        var value = Encoding.UTF8.GetString(payload, offset, byteLen);
        return (stringId, value);
    }
}
