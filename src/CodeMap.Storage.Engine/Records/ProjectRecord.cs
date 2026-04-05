namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>Fixed 32-byte binary layout for a project in projects.seg.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct ProjectRecord(
    int projectIntId, int nameStringId, int assemblyNameStringId,
    int targetFrameworkStringId, int outputTypeStringId, int flags,
    int reserved0 = 0, int reserved1 = 0)
{
    public readonly int ProjectIntId            = projectIntId;
    public readonly int NameStringId            = nameStringId;
    public readonly int AssemblyNameStringId    = assemblyNameStringId;
    public readonly int TargetFrameworkStringId = targetFrameworkStringId;
    public readonly int OutputTypeStringId      = outputTypeStringId;
    public readonly int Flags                   = flags;
    public readonly int Reserved0               = reserved0;
    public readonly int Reserved1               = reserved1;
    // sizeof = 32
}
