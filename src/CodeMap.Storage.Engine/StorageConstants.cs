namespace CodeMap.Storage.Engine;

/// <summary>Magic numbers, page sizes, and format version constants for the v2 binary storage engine.</summary>
internal static class StorageConstants
{
    public const uint SegmentMagic      = 0x434D_7632;  // 'CMv2'
    public const uint PageMagic         = 0xC04E_4D50;
    public const uint WalMagic          = 0xC04E_574C;
    public const uint CheckpointMagic   = 0xC04E_4350;
    public const int  PageSize          = 8192;
    public const int  PageHeaderSize    = 20;
    public const int  SegFileHeaderSize = 16;
    public const int  FormatMajor       = 2;
    public const int  FormatMinor       = 0;
}
