namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>Fixed 48-byte binary layout for a fact in facts.seg.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct FactRecord(
    int factIntId, int ownerSymbolIntId, int fileIntId, int spanStart, int spanEnd,
    int factKind, int primaryStringId, int secondaryStringId, int confidence,
    int flags, int reserved0 = 0, int reserved1 = 0)
{
    public readonly int FactIntId         = factIntId;
    public readonly int OwnerSymbolIntId  = ownerSymbolIntId;
    public readonly int FileIntId         = fileIntId;
    public readonly int SpanStart         = spanStart;
    public readonly int SpanEnd           = spanEnd;
    public readonly int FactKind          = factKind;
    public readonly int PrimaryStringId   = primaryStringId;
    public readonly int SecondaryStringId = secondaryStringId;
    public readonly int Confidence        = confidence;
    public readonly int Flags             = flags;
    public readonly int Reserved0         = reserved0;
    public readonly int Reserved1         = reserved1;
    // sizeof = 48
}
