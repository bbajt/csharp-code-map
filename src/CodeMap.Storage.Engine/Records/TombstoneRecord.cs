namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Fixed 16-byte binary layout for a tombstone in overlay WAL/snapshot.
/// Used to hide baseline or overlay-local entities during merged reads.
/// See STORAGE-FORMAT.MD §10.1 (updated per design review C-004).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct TombstoneRecord
{
    /// <summary>Entity kind: 0=Symbol, 1=Edge, 2=Fact, 3=File.</summary>
    public readonly int EntityKind;

    /// <summary>IntId of entity being hidden. Positive=baseline, negative=overlay-local.</summary>
    public readonly int EntityIntId;

    /// <summary>For symbols: StableId string interned in dictionary. For other entity kinds: 0.</summary>
    public readonly int StableIdStringId;

    /// <summary>Bit 0: TargetsBaseline (1=baseline entity, 0=overlay-local entity).</summary>
    public readonly int Flags;
    // sizeof = 16
}
