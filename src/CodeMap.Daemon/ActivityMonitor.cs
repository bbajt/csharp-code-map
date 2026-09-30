namespace CodeMap.Daemon;

using CodeMap.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// The process's <see cref="IActivityMonitor"/> (PHASE-21-12 T02). Thread-safe: requests are dispatched on
/// the MCP loop, while <see cref="IdleMemoryReclaimer"/> reads the state from a timer thread.
/// </summary>
public sealed class ActivityMonitor : IActivityMonitor
{
    private readonly TimeProvider _time;
    private readonly ILogger<ActivityMonitor> _logger;
    private readonly Lock _gate = new();
    private int _inFlight;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset? _lastHeavyWork;

    /// <summary>Creates the monitor; <paramref name="time"/> defaults to the system clock.</summary>
    public ActivityMonitor(ILogger<ActivityMonitor> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastActivity = _time.GetUtcNow();
    }

    /// <inheritdoc/>
    public IDisposable BeginRequest()
    {
        lock (_gate)
        {
            _inFlight++;
            _lastActivity = _time.GetUtcNow();
        }
        return new RequestScope(this);
    }

    /// <inheritdoc/>
    public void MarkHeavyWork(string reason)
    {
        lock (_gate) _lastHeavyWork = _time.GetUtcNow();
        _logger.LogDebug("Heavy work marked: {Reason}", reason);
    }

    /// <inheritdoc/>
    public DateTimeOffset LastActivityUtc { get { lock (_gate) return _lastActivity; } }

    /// <inheritdoc/>
    public DateTimeOffset? LastHeavyWorkUtc { get { lock (_gate) return _lastHeavyWork; } }

    /// <inheritdoc/>
    public bool RequestInFlight { get { lock (_gate) return _inFlight > 0; } }

    private void EndRequest()
    {
        lock (_gate)
        {
            _inFlight--;
            _lastActivity = _time.GetUtcNow();
        }
    }

    /// <summary>Ends its request once, on the first dispose.</summary>
    private sealed class RequestScope(ActivityMonitor owner) : IDisposable
    {
        private int _disposed;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndRequest();
        }
    }
}
