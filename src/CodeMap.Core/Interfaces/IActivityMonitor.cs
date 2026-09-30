namespace CodeMap.Core.Interfaces;

/// <summary>
/// Request activity and "heavy work happened" signals for idle memory reclamation (PHASE-21-12 T02,
/// ADR-061). One instance per process (registered by the Daemon). <c>McpServer</c> wraps every dispatched
/// request in <see cref="BeginRequest"/>; code that allocates heavily (a baseline build, a cold
/// <c>Solution</c> open) calls <see cref="MarkHeavyWork"/>.
/// </summary>
public interface IActivityMonitor
{
    /// <summary>Marks a request as in flight until the returned handle is disposed.</summary>
    IDisposable BeginRequest();

    /// <summary>Records that memory-heavy work just finished; <paramref name="reason"/> is logged.</summary>
    void MarkHeavyWork(string reason);

    /// <summary>When the last request started or finished (process start if none yet).</summary>
    DateTimeOffset LastActivityUtc { get; }

    /// <summary>When heavy work was last marked, or <c>null</c> if never.</summary>
    DateTimeOffset? LastHeavyWorkUtc { get; }

    /// <summary>Whether a request is being processed right now.</summary>
    bool RequestInFlight { get; }
}
