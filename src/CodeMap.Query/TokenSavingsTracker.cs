namespace CodeMap.Query;

using System.Text.Json;
using CodeMap.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Token savings accumulator. Thread-safe and safe across multiple processes sharing one
/// <c>~/.codemap/_savings.json</c>.
/// <para>
/// Each instance tracks only its own unsaved delta. <see cref="SaveToDisk"/> takes a
/// cross-process lock, re-reads the file, adds the delta, and atomically replaces the file —
/// so it never writes a stale absolute total. Saves also happen periodically from
/// <see cref="RecordSaving"/>, so a hard-killed process loses at most one interval of savings.
/// </para>
/// </summary>
public sealed class TokenSavingsTracker : ITokenSavingsTracker
{
    private static readonly TimeSpan DefaultSaveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly string? _savingsPath;
    private readonly ILogger? _logger;
    private readonly TimeSpan _saveInterval;
    private long _lastSaveTick = Environment.TickCount64;

    // Last known on-disk state (includes everything already saved, by any process).
    private long _diskTokens;
    private Dictionary<string, decimal> _diskCost = new();
    private SortedDictionary<string, long> _diskDaily = new(StringComparer.Ordinal);

    // This process's unsaved delta.
    private long _pendingTokens;
    private readonly Dictionary<string, decimal> _pendingCost = new();
    private readonly SortedDictionary<string, long> _pendingDaily = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Creates a tracker. If <paramref name="codeMapDir"/> is provided, loads existing totals
    /// from <c>_savings.json</c> on construction and enables persistence.
    /// </summary>
    /// <param name="codeMapDir">Directory holding <c>_savings.json</c>; null disables persistence.</param>
    /// <param name="logger">Receives persistence failures (never thrown).</param>
    /// <param name="saveInterval">Minimum time between automatic saves; default 10s. Zero saves on every record.</param>
    public TokenSavingsTracker(string? codeMapDir = null, ILogger? logger = null, TimeSpan? saveInterval = null)
    {
        _logger = logger;
        _saveInterval = saveInterval ?? DefaultSaveInterval;
        if (codeMapDir is not null)
        {
            _savingsPath = Path.Combine(codeMapDir, "_savings.json");
            var data = TryRead(out _);
            if (data is not null) ApplyDisk(data);
        }
    }

    /// <inheritdoc/>
    public void RecordSaving(int tokensSaved, Dictionary<string, decimal> costAvoided)
    {
        bool due;
        lock (_gate)
        {
            _pendingTokens += tokensSaved;
            foreach (var (model, cost) in costAvoided)
                _pendingCost[model] = _pendingCost.GetValueOrDefault(model) + cost;
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _pendingDaily[day] = _pendingDaily.GetValueOrDefault(day) + tokensSaved;
            due = _savingsPath is not null
                && Environment.TickCount64 - _lastSaveTick >= (long)_saveInterval.TotalMilliseconds;
        }
        if (due) SaveToDisk();
    }

    /// <inheritdoc/>
    public long TotalTokensSaved
    {
        get { lock (_gate) return _diskTokens + _pendingTokens; }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, decimal> TotalCostAvoided
    {
        get { lock (_gate) return Merge(_diskCost, _pendingCost); }
    }

    /// <summary>
    /// Merges this instance's unsaved delta into <c>_savings.json</c> under a cross-process lock
    /// and an atomic replace. Failures are logged, never thrown. Call on shutdown and periodically.
    /// </summary>
    public void SaveToDisk()
    {
        if (_savingsPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savingsPath)!);
            using var fileLock = AcquireLock();
            if (fileLock is null) return;

            lock (_gate)
            {
                _lastSaveTick = Environment.TickCount64;
                var onDisk = TryRead(out var corrupt);
                if (corrupt)
                {
                    try { File.Copy(_savingsPath, _savingsPath + ".corrupt", overwrite: true); } catch { }
                }

                var tokens = (onDisk?.TokensSavedTotal ?? 0) + _pendingTokens;
                var cost = Merge(onDisk?.CostAvoidedTotal ?? new(), _pendingCost);
                var daily = new SortedDictionary<string, long>(
                    onDisk?.DailyTokensSaved ?? new(), StringComparer.Ordinal);
                foreach (var (day, t) in _pendingDaily)
                    daily[day] = daily.GetValueOrDefault(day) + t;

                var merged = new SavingsData(tokens, cost, DateTimeOffset.UtcNow, daily);
                var tmp = _savingsPath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(merged, _jsonOptions));
                File.Move(tmp, _savingsPath, overwrite: true);

                // Committed: the delta is now part of the disk state.
                ApplyDisk(merged);
                _pendingTokens = 0;
                _pendingCost.Clear();
                _pendingDaily.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist token savings to {Path}", _savingsPath);
        }
    }

    private void ApplyDisk(SavingsData data)
    {
        lock (_gate)
        {
            _diskTokens = data.TokensSavedTotal;
            _diskCost = new Dictionary<string, decimal>(data.CostAvoidedTotal ?? new());
            _diskDaily = new SortedDictionary<string, long>(
                data.DailyTokensSaved ?? new(), StringComparer.Ordinal);
        }
    }

    private SavingsData? TryRead(out bool corrupt)
    {
        corrupt = false;
        if (_savingsPath is null || !File.Exists(_savingsPath)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<SavingsData>(File.ReadAllText(_savingsPath), _jsonOptions);
            corrupt = data is null;
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            corrupt = ex is JsonException;
            _logger?.LogWarning(ex, "Could not read token savings from {Path}; treating as empty", _savingsPath);
            return null;
        }
    }

    private FileStream? AcquireLock()
    {
        var lockPath = _savingsPath + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Timed out waiting for token savings lock {Path}", lockPath);
                return null;
            }
        }
    }

    private static Dictionary<string, decimal> Merge(
        IReadOnlyDictionary<string, decimal> a, IReadOnlyDictionary<string, decimal> b)
    {
        var result = new Dictionary<string, decimal>(a);
        foreach (var (k, v) in b) result[k] = result.GetValueOrDefault(k) + v;
        return result;
    }

    private sealed record SavingsData(
        long TokensSavedTotal,
        Dictionary<string, decimal>? CostAvoidedTotal,
        DateTimeOffset LastUpdated,
        SortedDictionary<string, long>? DailyTokensSaved = null);
}
