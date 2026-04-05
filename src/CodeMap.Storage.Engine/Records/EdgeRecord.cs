namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>Fixed 44-byte binary layout for a reference edge in edges.seg.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct EdgeRecord(
    int edgeIntId, int fromSymbolIntId, int toSymbolIntId, int toNameStringId,
    int fileIntId, int spanStart, int spanEnd, short edgeKind, short resolutionState,
    int flags, int weight, int reserved = 0)
{
    public readonly int   EdgeIntId        = edgeIntId;
    public readonly int   FromSymbolIntId  = fromSymbolIntId;
    public readonly int   ToSymbolIntId    = toSymbolIntId;
    public readonly int   ToNameStringId   = toNameStringId;
    public readonly int   FileIntId        = fileIntId;
    public readonly int   SpanStart        = spanStart;
    public readonly int   SpanEnd          = spanEnd;
    public readonly short EdgeKind         = edgeKind;
    public readonly short ResolutionState  = resolutionState;
    public readonly int   Flags            = flags;
    public readonly int   Weight           = weight;
    public readonly int   Reserved         = reserved;
    // sizeof = 44
}
