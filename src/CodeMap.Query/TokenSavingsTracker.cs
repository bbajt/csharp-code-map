namespace CodeMap.Query;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.Core.Interfaces;

/// <summary>
/// Session-scoped token savings accumulator. Thread-safe.
/// Persists totals to ~/.codemap/_savings.json on shutdown.
/// </summary>
public sealed class TokenSavingsTracker : ITokenSavingsTracker
{
    private long _totalTokens;
    private readonly ConcurrentDictionary<string, decimal> _totalCost = new();
    private readonly string? _savingsPath;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Creates a tracker. If <paramref name="codeMapDir"/> is provided, loads existing totals
    /// from <c>_savings.json</c> on construction and enables <see cref="SaveToDisk"/>.
    /// </summary>
    public TokenSavingsTracker(string? codeMapDir = null)
    {
        if (codeMapDir is not null)
        {
            _savingsPath = Path.Combine(codeMapDir, "_savings.json");
            LoadFromDisk();
        }
    }

    /// <inheritdoc/>
    public void RecordSaving(int tokensSaved, Dictionary<string, decimal> costAvoided)
    {
        Interlocked.Add(ref _totalTokens, tokensSaved);
        foreach (var (model, cost) in costAvoided)
            _totalCost.AddOrUpdate(model, cost, (_, existing) => existing + cost);
    }

    /// <inheritdoc/>
    public long TotalTokensSaved => Interlocked.Read(ref _totalTokens);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, decimal> TotalCostAvoided => _totalCost;

    /// <summary>
    /// Writes current totals to disk. Best-effort — exceptions are swallowed.
    /// Call on graceful shutdown.
    /// </summary>
    public void SaveToDisk()
    {
        if (_savingsPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savingsPath)!);
            var data = new SavingsData(
                Interlocked.Read(ref _totalTokens),
                _totalCost.ToDictionary(),
                DateTimeOffset.UtcNow);
            File.WriteAllText(_savingsPath, JsonSerializer.Serialize(data, _jsonOptions));
        }
        catch
        {
            // Best-effort — never crash on save failure
        }
    }

    private void LoadFromDisk()
    {
        if (_savingsPath is null || !File.Exists(_savingsPath)) return;
        try
        {
            var json = File.ReadAllText(_savingsPath);
            var data = JsonSerializer.Deserialize<SavingsData>(json, _jsonOptions);
            if (data is not null)
            {
                Interlocked.Exchange(ref _totalTokens, data.TokensSavedTotal);
                foreach (var (model, cost) in data.CostAvoidedTotal)
                    _totalCost[model] = cost;
            }
        }
        catch
        {
            // Corrupt file — start from zero
        }
    }

    private sealed record SavingsData(
        long TokensSavedTotal,
        Dictionary<string, decimal> CostAvoidedTotal,
        DateTimeOffset LastUpdated);
}
