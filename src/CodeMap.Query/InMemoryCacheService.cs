namespace CodeMap.Query;

using System.Collections.Concurrent;
using CodeMap.Core.Interfaces;

/// <summary>
/// L1 in-memory cache with optional TTL and a configurable max entry count.
/// Thread-safe via ConcurrentDictionary.
/// Expired entries are lazily evicted on GetAsync. When the cache reaches
/// capacity, expired entries are removed first, then the oldest 10% by
/// last-access time (approximate LRU, approximate O(n)).
/// </summary>
public sealed class InMemoryCacheService : ICacheService
{
    // Reference type so we can mutate LastAccessed without replacing the dict entry.
    private sealed class CacheEntry
    {
        public object Value { get; }
        public DateTimeOffset ExpiresAt { get; }
        private long _lastAccessedTicks;
        public DateTimeOffset LastAccessed
        {
            get => new(Interlocked.Read(ref _lastAccessedTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastAccessedTicks, value.UtcTicks);
        }

        public CacheEntry(object value, DateTimeOffset expiresAt)
        {
            Value = value;
            ExpiresAt = expiresAt;
            _lastAccessedTicks = DateTimeOffset.UtcNow.UtcTicks;
        }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;

    /// <summary>
    /// Creates a new cache with the given capacity and TTL.
    /// </summary>
    /// <param name="maxEntries">Maximum number of cached entries (default 10,000). When exceeded, LRU eviction runs.</param>
    /// <param name="defaultTtl">TTL per entry (default 10 minutes).</param>
    public InMemoryCacheService(int maxEntries = 10_000, TimeSpan? defaultTtl = null)
    {
        _maxEntries = maxEntries;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(10);
    }

    /// <inheritdoc/>
    public Task<T?> GetAsync<T>(string cacheKey, CancellationToken ct = default) where T : class
    {
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                entry.LastAccessed = DateTimeOffset.UtcNow;
                return Task.FromResult((T?)entry.Value);
            }
            _cache.TryRemove(cacheKey, out _);
        }
        return Task.FromResult<T?>(null);
    }

    /// <inheritdoc/>
    public Task SetAsync<T>(string cacheKey, T value, CancellationToken ct = default) where T : class
    {
        if (_cache.Count >= _maxEntries)
        {
            EvictExpiredAndOldest();
            // Re-check post-eviction: concurrent writers may still push us over.
            // Dropping is acceptable — approximate LRU is the documented behaviour.
            if (_cache.Count >= _maxEntries)
                return Task.CompletedTask;
        }

        _cache[cacheKey] = new CacheEntry(value, DateTimeOffset.UtcNow + _defaultTtl);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InvalidateAsync(string keyPrefix, CancellationToken ct = default)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
                _cache.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InvalidateAllAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        return Task.CompletedTask;
    }

    private void EvictExpiredAndOldest()
    {
        var now = DateTimeOffset.UtcNow;

        // First pass: remove expired entries
        var expired = _cache
            .Where(kv => kv.Value.ExpiresAt <= now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
            _cache.TryRemove(key, out _);

        // If still at or over capacity, evict oldest 10% by last-access time
        if (_cache.Count >= _maxEntries)
        {
            var toRemove = _cache
                .OrderBy(kv => kv.Value.LastAccessed)
                .Take(Math.Max(1, _cache.Count / 10))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toRemove)
                _cache.TryRemove(key, out _);
        }
    }
}
