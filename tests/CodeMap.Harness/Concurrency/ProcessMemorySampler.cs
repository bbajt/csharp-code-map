namespace CodeMap.Harness.Concurrency;

using System.Diagnostics;

/// <summary>
/// Samples working set + private bytes of a fixed set of daemon processes on a timer.
/// Keeps every sample (timestamped from sampler start) so the runner can derive peak,
/// steady-state (loop window) and post-idle figures afterwards. Exited processes count as 0.
/// </summary>
public sealed class ProcessMemorySampler : IAsyncDisposable
{
    private readonly Process[] _processes;
    private readonly long[] _peakPerProcess;
    private readonly List<MemorySample> _samples = [];
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <summary>Starts sampling <paramref name="processIds"/> every <paramref name="interval"/>.</summary>
    public ProcessMemorySampler(IReadOnlyList<int> processIds, TimeSpan interval)
    {
        _processes = processIds.Select(TryOpen).OfType<Process>().ToArray();
        _peakPerProcess = new long[_processes.Length];
        _loop = LoopAsync(interval, _stop.Token);
    }

    /// <summary>Elapsed time since the sampler started (the sample time base).</summary>
    public TimeSpan Now => _clock.Elapsed;

    /// <summary>Takes one sample immediately, records it, and returns it.</summary>
    public MemorySample SampleNow()
    {
        long ws = 0, priv = 0;
        lock (_lock)
        {
            for (var i = 0; i < _processes.Length; i++)
            {
                var (pws, ppriv) = Read(_processes[i]);
                ws += pws;
                priv += ppriv;
                if (pws > _peakPerProcess[i]) _peakPerProcess[i] = pws;
            }
            var sample = new MemorySample(_clock.Elapsed, ws, priv);
            _samples.Add(sample);
            return sample;
        }
    }

    /// <summary>
    /// Summarises the samples. Steady state = median total working set of samples in
    /// the second half of <c>[loopStart, loopEnd]</c> (warm-up excluded).
    /// </summary>
    public MemoryStats Summarize(TimeSpan loopStart, TimeSpan loopEnd, MemorySample postIdle)
    {
        lock (_lock)
        {
            var steadyFrom = loopStart + (loopEnd - loopStart) / 2;
            var steady = _samples
                .Where(s => s.At >= steadyFrom && s.At <= loopEnd)
                .Select(s => s.TotalWorkingSetBytes)
                .Order()
                .ToArray();

            return new MemoryStats(
                PeakTotalWorkingSetBytes: _samples.Count == 0 ? 0 : _samples.Max(s => s.TotalWorkingSetBytes),
                SteadyTotalWorkingSetBytes: steady.Length == 0 ? 0 : steady[steady.Length / 2],
                PostIdleTotalWorkingSetBytes: postIdle.TotalWorkingSetBytes,
                PeakTotalPrivateBytes: _samples.Count == 0 ? 0 : _samples.Max(s => s.TotalPrivateBytes),
                PerProcessPeakWorkingSetBytes: [.. _peakPerProcess],
                SampleCount: _samples.Count);
        }
    }

    /// <summary>Stops the sampling loop and releases process handles.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
        foreach (var p in _processes) p.Dispose();
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            SampleNow();
    }

    private static (long WorkingSet, long Private) Read(Process p)
    {
        try
        {
            p.Refresh();
            return p.HasExited ? (0, 0) : (p.WorkingSet64, p.PrivateMemorySize64);
        }
        catch (InvalidOperationException) { return (0, 0); }
    }

    private static Process? TryOpen(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>One memory sample: total working set and private bytes across all sampled processes.</summary>
public sealed record MemorySample(TimeSpan At, long TotalWorkingSetBytes, long TotalPrivateBytes);
