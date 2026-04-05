namespace CodeMap.Storage.Engine.Tests;

using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

public sealed class SearchIndexBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-search-test-{Guid.NewGuid():N}");

    public SearchIndexBuilderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Tokenize_CamelCase_SplitsCorrectly()
    {
        var tokens = SearchIndexBuilder.Tokenize(
            "T:MyApp.Orders.OrderRepository",
            "OrderRepository",
            "MyApp.Orders");

        tokens.Should().Contain("orderrepository");   // display name lowered
        tokens.Should().Contain("order");              // camelCase split
        tokens.Should().Contain("repository");         // camelCase split
        tokens.Should().Contain("orders");             // namespace segment
        tokens.Should().Contain("myapp");              // namespace segment
        tokens.Should().Contain("myapp.orders.orderrepository"); // FQN stripped
    }

    [Fact]
    public void Tokenize_SkipsSingleCharTokens()
    {
        var tokens = SearchIndexBuilder.Tokenize(
            "T:A.IGitService",
            "IGitService",
            "A");

        // "I" from camelCase split is 1 char — should be skipped
        tokens.Should().NotContain("i");
        tokens.Should().Contain("git");
        tokens.Should().Contain("service");
        // "A" namespace segment is 1 char — skipped
        tokens.Should().NotContain("a");
        // But full display name is kept
        tokens.Should().Contain("igitservice");
    }

    [Fact]
    public void Tokenize_StripsDocIdPrefix()
    {
        var tokens = SearchIndexBuilder.Tokenize(
            "M:MyApp.Foo.Bar(System.String)",
            "Bar",
            "MyApp");

        tokens.Should().Contain("myapp.foo.bar"); // stripped "M:" and "(System.String)"
        tokens.Should().NotContainMatch("*System.String*");
    }

    [Fact]
    public void Tokenize_StripsGenericArity()
    {
        var tokens = SearchIndexBuilder.Tokenize(
            "T:MyApp.GenericClass`2",
            "GenericClass",
            "MyApp");

        tokens.Should().Contain("myapp.genericclass"); // stripped "`2"
    }

    [Fact]
    public void Build_ProducesValidSearchIdx()
    {
        using var dictBuilder = new DictionaryBuilder();
        var symbols = new List<(int, string, string, string?, string?, string?)>
        {
            (1, "T:Ns.ClassA", "ClassA", "Ns", null, null),
            (2, "T:Ns.ClassB", "ClassB", "Ns", null, null),
            (3, "M:Ns.ClassA.DoWork", "DoWork", "Ns", null, null),
        };

        var dictPath = Path.Combine(_tempDir, "dictionary.seg");
        var searchPath = Path.Combine(_tempDir, "search.idx");

        // Pre-intern symbol strings so they get lower IDs
        foreach (var (_, fqn, display, ns, _, _) in symbols)
        {
            dictBuilder.Intern(fqn);
            dictBuilder.Intern(display);
            if (ns != null) dictBuilder.Intern(ns);
        }

        SearchIndexBuilder.Build(searchPath, symbols, dictBuilder);
        using var dict = dictBuilder.Build(dictPath);

        // Verify the file has correct header
        var headerBytes = File.ReadAllBytes(searchPath).AsSpan(0, StorageConstants.SegFileHeaderSize);
        var magic = BitConverter.ToUInt32(headerBytes);
        magic.Should().Be(StorageConstants.SegmentMagic);

        var recordCount = BitConverter.ToUInt32(headerBytes[8..]);
        recordCount.Should().BeGreaterThan(0, "should have tokens");
    }

    [Fact]
    public void Build_TokenEntriesSortedByStringId()
    {
        using var dictBuilder = new DictionaryBuilder();
        var symbols = new List<(int, string, string, string?, string?, string?)>
        {
            (1, "T:A.Foo", "Foo", "A", null, null),
            (2, "T:Z.Bar", "Bar", "Z", null, null),
        };

        var dictPath = Path.Combine(_tempDir, "dict.seg");
        var searchPath = Path.Combine(_tempDir, "search.idx");

        SearchIndexBuilder.Build(searchPath, symbols, dictBuilder);
        using var dict = dictBuilder.Build(dictPath);

        // Read TokenEntry array from file
        var fileBytes = File.ReadAllBytes(searchPath);
        var tokenCount = BitConverter.ToUInt32(fileBytes.AsSpan(8));
        var entrySize = Marshal.SizeOf<TokenEntry>();
        var entries = new TokenEntry[(int)tokenCount];

        for (var i = 0; i < (int)tokenCount; i++)
        {
            var offset = StorageConstants.SegFileHeaderSize + i * entrySize;
            entries[i] = MemoryMarshal.Read<TokenEntry>(fileBytes.AsSpan(offset));
        }

        // Verify sorted by TokenStringId
        for (var i = 1; i < entries.Length; i++)
            entries[i].TokenStringId.Should().BeGreaterThanOrEqualTo(entries[i - 1].TokenStringId);
    }

    [Fact]
    public void Build_PostingsDecodable()
    {
        using var dictBuilder = new DictionaryBuilder();
        // 3 symbols sharing token "order"
        var symbols = new List<(int, string, string, string?, string?, string?)>
        {
            (1, "T:Ns.OrderService", "OrderService", "Ns", null, null),
            (2, "T:Ns.OrderRepo", "OrderRepo", "Ns", null, null),
            (3, "T:Ns.OrderDto", "OrderDto", "Ns", null, null),
        };

        var dictPath = Path.Combine(_tempDir, "dict.seg");
        var searchPath = Path.Combine(_tempDir, "search.idx");

        SearchIndexBuilder.Build(searchPath, symbols, dictBuilder);
        using var dict = dictBuilder.Build(dictPath);

        // Find the "order" token
        dict.TryFind("order", out var orderTokenId).Should().BeTrue();

        // Parse file to find the TokenEntry for "order"
        var fileBytes = File.ReadAllBytes(searchPath);
        var tokenCount = (int)BitConverter.ToUInt32(fileBytes.AsSpan(8));
        var entrySize = Marshal.SizeOf<TokenEntry>();
        TokenEntry? orderEntry = null;

        for (var i = 0; i < tokenCount; i++)
        {
            var offset = StorageConstants.SegFileHeaderSize + i * entrySize;
            var entry = MemoryMarshal.Read<TokenEntry>(fileBytes.AsSpan(offset));
            if (entry.TokenStringId == orderTokenId)
            {
                orderEntry = entry;
                break;
            }
        }

        orderEntry.Should().NotBeNull();
        orderEntry!.Value.PostingCount.Should().Be(3);

        // Decode the postings block
        var postingsBase = StorageConstants.SegFileHeaderSize + tokenCount * entrySize;
        var blockStart = postingsBase + (int)orderEntry.Value.BlockOffset;
        var readOffset = blockStart;
        var decoded = new List<int>();
        uint running = 0;
        for (var i = 0; i < (int)orderEntry.Value.PostingCount; i++)
        {
            running += Leb128.Read(fileBytes, ref readOffset);
            decoded.Add((int)running);
        }

        decoded.Should().BeEquivalentTo([1, 2, 3]);
    }
}
