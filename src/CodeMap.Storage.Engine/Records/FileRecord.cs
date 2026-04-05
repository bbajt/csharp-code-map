namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>Fixed 48-byte binary layout for a file in files.seg.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct FileRecord(
    int fileIntId, int pathStringId, int normalizedStringId, int projectIntId,
    long contentHashHigh, long contentHashLow, short language, short flags,
    int contentId, int reserved0 = 0, int reserved1 = 0)
{
    public readonly int   FileIntId          = fileIntId;
    public readonly int   PathStringId       = pathStringId;
    public readonly int   NormalizedStringId = normalizedStringId;
    public readonly int   ProjectIntId       = projectIntId;
    public readonly long  ContentHashHigh    = contentHashHigh;
    public readonly long  ContentHashLow     = contentHashLow;
    public readonly short Language           = language;
    public readonly short Flags              = flags;
    public readonly int   ContentId          = contentId;
    public readonly int   Reserved0          = reserved0;
    public readonly int   Reserved1          = reserved1;
    // sizeof = 48
}
