namespace CodeMap.Core.Interfaces;

/// <summary>
/// L1 in-memory cache for query results.
/// Implementation: CodeMap.Query.
/// </summary>
public interface ICacheService
{
    /// <summary>Attempts to get a cached value. Returns null on miss.</summary>
    Task<T?> GetAsync<T>(string cacheKey, CancellationToken ct = default) where T : class;

    /// <summary>Stores a value in the cache.</summary>
    Task SetAsync<T>(string cacheKey, T value, CancellationToken ct = default) where T : class;

    /// <summary>Invalidates all entries matching the given prefix.</summary>
    Task InvalidateAsync(string keyPrefix, CancellationToken ct = default);

    /// <summary>Invalidates the entire cache.</summary>
    Task InvalidateAllAsync(CancellationToken ct = default);
}
