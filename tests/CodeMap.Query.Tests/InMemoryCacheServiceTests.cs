namespace CodeMap.Query.Tests;

using CodeMap.Query;
using FluentAssertions;

public class InMemoryCacheServiceTests
{
    private readonly InMemoryCacheService _cache = new();

    [Fact]
    public async Task Get_MissOnEmptyCache_ReturnsNull()
    {
        var result = await _cache.GetAsync<string>("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsValue()
    {
        await _cache.SetAsync("key1", "hello");
        var result = await _cache.GetAsync<string>("key1");
        result.Should().Be("hello");
    }

    [Fact]
    public async Task Set_ThenGet_DifferentKey_ReturnsNull()
    {
        await _cache.SetAsync("key1", "hello");
        var result = await _cache.GetAsync<string>("key2");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_SameKeyTwice_OverwritesPreviousValue()
    {
        await _cache.SetAsync("key", "first");
        await _cache.SetAsync("key", "second");
        var result = await _cache.GetAsync<string>("key");
        result.Should().Be("second");
    }

    [Fact]
    public async Task Get_ExpiredEntry_ReturnsNull()
    {
        var cache = new InMemoryCacheService(defaultTtl: TimeSpan.FromMilliseconds(1));
        await cache.SetAsync("key", "value");
        await Task.Delay(10); // let it expire
        var result = await cache.GetAsync<string>("key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_ByPrefix_RemovesMatchingEntries()
    {
        await _cache.SetAsync("repo1:sha:search:abc", "v1");
        await _cache.SetAsync("repo1:sha:card:sym", "v2");
        await _cache.SetAsync("repo2:sha:search:xyz", "v3");

        await _cache.InvalidateAsync("repo1:");

        (await _cache.GetAsync<string>("repo1:sha:search:abc")).Should().BeNull();
        (await _cache.GetAsync<string>("repo1:sha:card:sym")).Should().BeNull();
        (await _cache.GetAsync<string>("repo2:sha:search:xyz")).Should().Be("v3");
    }

    [Fact]
    public async Task Invalidate_ByPrefix_KeepsNonMatchingEntries()
    {
        await _cache.SetAsync("repo1:key", "kept");
        await _cache.SetAsync("repo2:key", "removed");

        await _cache.InvalidateAsync("repo2:");

        (await _cache.GetAsync<string>("repo1:key")).Should().Be("kept");
    }

    [Fact]
    public async Task InvalidateAll_ClearsEverything()
    {
        await _cache.SetAsync("a", "1");
        await _cache.SetAsync("b", "2");
        await _cache.SetAsync("c", "3");

        await _cache.InvalidateAllAsync();

        (await _cache.GetAsync<string>("a")).Should().BeNull();
        (await _cache.GetAsync<string>("b")).Should().BeNull();
        (await _cache.GetAsync<string>("c")).Should().BeNull();
    }

    [Fact]
    public async Task Get_ConcurrentAccess_NoExceptions()
    {
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            await _cache.SetAsync($"key{i}", $"value{i}");
            await _cache.GetAsync<string>($"key{i % 10}");
        });

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }
}
