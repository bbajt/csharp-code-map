namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>Fixed 64-byte binary layout for a symbol in symbols.seg.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct SymbolRecord(
    int symbolIntId, int stableIdStringId, int fqnStringId, int displayNameStringId,
    int namespaceStringId, int containerIntId, int fileIntId, int projectIntId,
    short kind, short accessibility, int flags, int spanStart, int spanEnd,
    int nameTokensStringId, int signatureHash, int reserved0 = 0, int reserved1 = 0)
{
    public readonly int   SymbolIntId         = symbolIntId;
    public readonly int   StableIdStringId    = stableIdStringId;
    public readonly int   FqnStringId         = fqnStringId;
    public readonly int   DisplayNameStringId = displayNameStringId;
    public readonly int   NamespaceStringId   = namespaceStringId;
    public readonly int   ContainerIntId      = containerIntId;
    public readonly int   FileIntId           = fileIntId;
    public readonly int   ProjectIntId        = projectIntId;
    public readonly short Kind                = kind;
    public readonly short Accessibility       = accessibility;
    public readonly int   Flags               = flags;
    public readonly int   SpanStart           = spanStart;
    public readonly int   SpanEnd             = spanEnd;
    public readonly int   NameTokensStringId  = nameTokensStringId;
    public readonly int   SignatureHash       = signatureHash;
    public readonly int   Reserved0           = reserved0;
    public readonly int   Reserved1           = reserved1;
    // sizeof = 64
}
