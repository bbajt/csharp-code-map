namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// 12-byte entry in the search.idx token header table.
/// Sorted by TokenStringId ascending for binary search.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct TokenEntry(int tokenStringId, uint blockOffset, uint postingCount)
{
    public readonly int  TokenStringId = tokenStringId;
    public readonly uint BlockOffset   = blockOffset;
    public readonly uint PostingCount  = postingCount;
    // sizeof = 12
}
