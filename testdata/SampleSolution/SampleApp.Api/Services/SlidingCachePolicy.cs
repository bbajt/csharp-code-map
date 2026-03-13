namespace SampleApp.Api.Services;

/// <summary>Cache policy that expires entries based on time since last access.</summary>
public class SlidingCachePolicy : ICachePolicy
{
    public TimeSpan SlidingExpiration { get; }

    public SlidingCachePolicy(TimeSpan slidingExpiration)
    {
        if (slidingExpiration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(slidingExpiration), "Must be positive.");
        SlidingExpiration = slidingExpiration;
    }

    public bool IsExpired(DateTimeOffset lastAccessed)
        => DateTimeOffset.UtcNow - lastAccessed > SlidingExpiration;
}
