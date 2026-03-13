namespace SampleApp.Api.Services;

/// <summary>Contract for cache expiration policies.</summary>
public interface ICachePolicy
{
    /// <summary>The sliding expiration window after which an entry is considered stale.</summary>
    TimeSpan SlidingExpiration { get; }

    /// <summary>Determines whether a cache entry has expired given its last access time.</summary>
    bool IsExpired(DateTimeOffset lastAccessed);
}
