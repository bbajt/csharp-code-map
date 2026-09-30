namespace CodeMap.Core.Models;

using System.Diagnostics;

/// <summary>
/// Process and GC memory at one moment (PHASE-21-12). Logged as a <c>MEMORY_SNAPSHOT</c> line after heavy
/// work (a baseline build, a cold <c>Solution</c> open) and around idle reclamation, so the idle-memory
/// breakdown is visible in <c>~/.codemap/logs</c> without a profiler.
/// </summary>
/// <param name="WorkingSetBytes">Process working set (includes file-backed mapped segments the OS can drop).</param>
/// <param name="PrivateBytes">Process private bytes: memory only this process pays for.</param>
/// <param name="GcHeapBytes">Managed heap size at the last GC (<see cref="GCMemoryInfo.HeapSizeBytes"/>).</param>
/// <param name="GcFragmentedBytes">Free space inside the managed heap at the last GC.</param>
/// <param name="GcCommittedBytes">Memory the GC has committed from the OS.</param>
public sealed record MemorySnapshot(
    long WorkingSetBytes,
    long PrivateBytes,
    long GcHeapBytes,
    long GcFragmentedBytes,
    long GcCommittedBytes)
{
    /// <summary>
    /// Structured log template; arguments in order: event, then the five figures in MB
    /// (see <see cref="LogArgs"/>).
    /// </summary>
    public const string LogTemplate =
        "MEMORY_SNAPSHOT {Event} ws_mb={WorkingSetMb} private_mb={PrivateMb} heap_mb={HeapMb} fragmented_mb={FragmentedMb} committed_mb={CommittedMb}";

    /// <summary>Reads the current process and <see cref="GC.GetGCMemoryInfo()"/>. No state is kept.</summary>
    public static MemorySnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new MemorySnapshot(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            gc.HeapSizeBytes,
            gc.FragmentedBytes,
            gc.TotalCommittedBytes);
    }

    /// <summary>The arguments for <see cref="LogTemplate"/>: <paramref name="eventName"/>, then MB figures.</summary>
    public object[] LogArgs(string eventName) =>
        [eventName, Mb(WorkingSetBytes), Mb(PrivateBytes), Mb(GcHeapBytes), Mb(GcFragmentedBytes), Mb(GcCommittedBytes)];

    private static long Mb(long bytes) => bytes / (1024 * 1024);
}
