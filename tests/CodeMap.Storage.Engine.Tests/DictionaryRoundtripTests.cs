namespace CodeMap.Storage.Engine.Tests;

using FluentAssertions;
using Xunit;

public sealed class DictionaryRoundtripTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-dict-test-{Guid.NewGuid():N}");

    public DictionaryRoundtripTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string DictPath => Path.Combine(_tempDir, "dictionary.seg");

    [Fact]
    public void EmptyDictionary_CountIsZero()
    {
        using var builder = new DictionaryBuilder();
        using var reader = builder.Build(DictPath);

        reader.Count.Should().Be(0);
    }

    [Fact]
    public void Intern1000Strings_AllResolveCorrectly()
    {
        var strings = Enumerable.Range(1, 1000).Select(i => $"string_{i}_value").ToList();

        using var builder = new DictionaryBuilder();
        var ids = strings.Select(s => builder.Intern(s)).ToList();

        builder.Count.Should().Be(1000);

        using var reader = builder.Build(DictPath);
        reader.Count.Should().Be(1000);

        for (var i = 0; i < strings.Count; i++)
            reader.Resolve(ids[i]).Should().Be(strings[i]);
    }

    [Fact]
    public void InternDuplicate_ReturnsSameId()
    {
        using var builder = new DictionaryBuilder();
        var id1 = builder.Intern("hello");
        var id2 = builder.Intern("hello");

        id1.Should().Be(id2);
        builder.Count.Should().Be(1);
    }

    [Fact]
    public void StringIdZero_ReturnsEmptyString()
    {
        using var builder = new DictionaryBuilder();
        builder.Intern("something");
        using var reader = builder.Build(DictPath);

        reader.Resolve(0).Should().BeEmpty();
        reader.ResolveUtf8(0).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void OutOfRangeStringId_ThrowsStorageFormatException()
    {
        using var builder = new DictionaryBuilder();
        builder.Intern("one");
        using var reader = builder.Build(DictPath);

        var act = () => reader.Resolve(999);
        act.Should().Throw<StorageFormatException>();
    }

    [Fact]
    public void NegativeStringId_ThrowsStorageFormatException()
    {
        using var builder = new DictionaryBuilder();
        builder.Intern("one");
        using var reader = builder.Build(DictPath);

        var act = () => reader.Resolve(-1);
        act.Should().Throw<StorageFormatException>();
    }

    [Fact]
    public void TryFind_KnownString_ReturnsCorrectId()
    {
        using var builder = new DictionaryBuilder();
        var id = builder.Intern("findme");
        builder.Intern("other");
        using var reader = builder.Build(DictPath);

        reader.TryFind("findme", out var foundId).Should().BeTrue();
        foundId.Should().Be(id);
    }

    [Fact]
    public void TryFind_UnknownString_ReturnsFalse()
    {
        using var builder = new DictionaryBuilder();
        builder.Intern("exists");
        using var reader = builder.Build(DictPath);

        reader.TryFind("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void InternEmptyString_ReturnsZero()
    {
        using var builder = new DictionaryBuilder();
        builder.Intern("").Should().Be(0);
        builder.Intern((string)null!).Should().Be(0);
        builder.Count.Should().Be(0);
    }

    [Fact]
    public void InternUtf8Span_RoundTrips()
    {
        using var builder = new DictionaryBuilder();
        var utf8 = System.Text.Encoding.UTF8.GetBytes("utf8test");
        var id = builder.Intern(utf8.AsSpan());

        using var reader = builder.Build(DictPath);
        reader.Resolve(id).Should().Be("utf8test");
    }

    [Fact]
    public void UnicodeStrings_RoundTripCorrectly()
    {
        using var builder = new DictionaryBuilder();
        var id1 = builder.Intern("日本語テスト");
        var id2 = builder.Intern("émojis: 🎉");
        using var reader = builder.Build(DictPath);

        reader.Resolve(id1).Should().Be("日本語テスト");
        reader.Resolve(id2).Should().Be("émojis: 🎉");
    }

    [Fact]
    public void ResolveUtf8_ReturnsRawBytes()
    {
        using var builder = new DictionaryBuilder();
        var id = builder.Intern("raw");
        using var reader = builder.Build(DictPath);

        var span = reader.ResolveUtf8(id);
        System.Text.Encoding.UTF8.GetString(span).Should().Be("raw");
    }

    [Fact]
    public void IdsAreOneBased()
    {
        using var builder = new DictionaryBuilder();
        var first = builder.Intern("alpha");
        var second = builder.Intern("beta");

        first.Should().Be(1);
        second.Should().Be(2);
    }

    [Fact]
    public void BuildTwice_Throws()
    {
        using var builder = new DictionaryBuilder();
        builder.Intern("x");
        using var reader = builder.Build(DictPath);

        var act = () => builder.Build(DictPath + ".2");
        act.Should().Throw<ObjectDisposedException>();
    }
}
