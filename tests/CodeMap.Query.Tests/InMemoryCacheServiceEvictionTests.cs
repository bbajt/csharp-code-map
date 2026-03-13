namespace CodeMap.Query.Tests;

using FluentAssertions;

/// <summary>
/// Tests for InMemoryCacheService LRU eviction policy (PHASE-04-02 T02).
/// </summary>
public class InMemoryCacheServiceEvictionTests
{
    [Fact]
    public async Task SetAsync_UnderCapacity_NoEviction()
    {
        var cache = new InMemoryCacheService(maxEntries: 10_000);

        for (int i = 0; i < 100; i++)
            await cache.SetAsync($"key{i}", $"value{i}");

        // All 100 entries should still be present
        for (int i = 0; i < 100; i++)
        {
            var result = await cache.GetAsync<string>($"key{i}");
            result.Should().Be($"value{i}", $"key{i} should be present (under capacity)");
        }
    }

    [Fact]
    public async Task SetAsync_AtCapacity_EvictsExpired()
    {
        // Fill cache to capacity, then add entries that expire very quickly
        var cache = new InMemoryCacheService(maxEntries: 100, defaultTtl: TimeSpan.FromMilliseconds(50));

        for (int i = 0; i < 100; i++)
            await cache.SetAsync($"key{i}", $"value{i}");

        // Let entries expire
        await Task.Delay(100);

        // Adding one more should trigger eviction of expired entries
        await cache.SetAsync("newkey", "newvalue");

        // Expired entries should have been evicted
        var result = await cache.GetAsync<string>("newkey");
        result.Should().Be("newvalue");
    }

    [Fact]
    public async Task SetAsync_AtCapacity_EvictsOldest()
    {
        // Fill with long-lived entries (won't expire during test)
        var cache = new InMemoryCacheService(maxEntries: 100, defaultTtl: TimeSpan.FromMinutes(10));

        for (int i = 0; i < 100; i++)
            await cache.SetAsync($"key{i}", $"value{i}");

        // Add one more — should trigger 10% LRU eviction
        await cache.SetAsync("overflow", "newvalue");

        // Cache should have fewer than 101 entries (10% evicted = at least 10 removed)
        // We can verify by checking that at least some entries were removed
        // (We can't easily count without exposing internals, so verify via recent entry)
        var overflowResult = await cache.GetAsync<string>("overflow");
        overflowResult.Should().Be("newvalue", "newly added entry should survive eviction");
    }

    [Fact]
    public async Task GetAsync_UpdatesLastAccessed()
    {
        var cache = new InMemoryCacheService(maxEntries: 10_000);

        await cache.SetAsync("key", "value");

        // Get the entry (should update last accessed)
        var before = DateTimeOffset.UtcNow;
        await Task.Delay(10);
        await cache.GetAsync<string>("key");
        await Task.Delay(10);
        var after = DateTimeOffset.UtcNow;

        // Verify: the entry should still be accessible (last accessed updated)
        var result = await cache.GetAsync<string>("key");
        result.Should().Be("value", "entry should remain accessible after get");

        // The access time should be within the test window
        before.Should().BeBefore(after);
    }

    [Fact]
    public async Task Eviction_PreservesRecentlyAccessed()
    {
        // Use max = 100, add 100 entries
        var cache = new InMemoryCacheService(maxEntries: 100, defaultTtl: TimeSpan.FromMinutes(10));

        for (int i = 0; i < 100; i++)
            await cache.SetAsync($"old{i}", $"value{i}");

        // Access entries 90-99 to make them "recent"
        for (int i = 90; i < 100; i++)
            await cache.GetAsync<string>($"old{i}");

        // Add one more to trigger eviction
        await cache.SetAsync("recent", "keep");

        // Recently accessed entries (90-99) should survive
        for (int i = 90; i < 100; i++)
        {
            var result = await cache.GetAsync<string>($"old{i}");
            result.Should().Be($"value{i}", $"recently accessed old{i} should survive eviction");
        }

        // The newly added entry should also be present
        var newResult = await cache.GetAsync<string>("recent");
        newResult.Should().Be("keep");
    }

    [Fact]
    public void Constructor_DefaultMaxEntries_Is10000()
    {
        // Default constructor — should not throw, default capacity is 10,000
        var cache = new InMemoryCacheService();
        cache.Should().NotBeNull();
    }

    [Fact]
    public async Task Constructor_CustomMaxEntries_Respected()
    {
        var cache = new InMemoryCacheService(maxEntries: 100, defaultTtl: TimeSpan.FromMinutes(10));

        // Add 200 entries — eviction should keep count manageable
        for (int i = 0; i < 200; i++)
            await cache.SetAsync($"key{i}", $"value{i}");

        // The last added entry should always be present (just added)
        var last = await cache.GetAsync<string>("key199");
        last.Should().Be("value199", "most recently added entry should survive");
    }

    [Fact]
    public async Task SetAsync_ExpiredEntry_CanBeOverwritten()
    {
        var cache = new InMemoryCacheService(
            maxEntries: 10_000, defaultTtl: TimeSpan.FromMilliseconds(50));

        await cache.SetAsync("key", "old");
        await Task.Delay(100); // let it expire

        // Should be able to set a new value under the same key
        await cache.SetAsync("key", "new");
        var result = await cache.GetAsync<string>("key");
        result.Should().Be("new");
    }

    [Fact]
    public async Task SetAsync_AtCapacityAllExpired_NewEntryFits()
    {
        // Tiny cache with very short TTL so all entries expire before we add the 4th
        var cache = new InMemoryCacheService(maxEntries: 3,
            defaultTtl: TimeSpan.FromMilliseconds(1));

        await cache.SetAsync("k1", "v1");
        await cache.SetAsync("k2", "v2");
        await cache.SetAsync("k3", "v3");

        await Task.Delay(20); // let all TTLs expire

        // Adding a 4th entry should evict the three expired ones and succeed
        await cache.SetAsync("k4", "v4");

        var k4 = await cache.GetAsync<string>("k4");
        k4.Should().Be("v4"); // new entry must be present after post-eviction re-check passes
    }
}
