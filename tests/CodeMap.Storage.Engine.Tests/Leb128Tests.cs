namespace CodeMap.Storage.Engine.Tests;

using FluentAssertions;
using Xunit;

public sealed class Leb128Tests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(16383u)]
    [InlineData(16384u)]
    [InlineData(uint.MaxValue)]
    public void RoundTrip_AllValues(uint value)
    {
        using var ms = new MemoryStream();
        Leb128.Write(ms, value);

        var bytes = ms.ToArray();
        var offset = 0;
        var decoded = Leb128.Read(bytes, ref offset);

        decoded.Should().Be(value);
        offset.Should().Be(bytes.Length);
    }

    [Fact]
    public void SmallValues_SingleByte()
    {
        using var ms = new MemoryStream();
        Leb128.Write(ms, 0);
        ms.Length.Should().Be(1);

        ms.SetLength(0);
        Leb128.Write(ms, 127);
        ms.Length.Should().Be(1);
    }

    [Fact]
    public void Value128_TwoBytes()
    {
        using var ms = new MemoryStream();
        Leb128.Write(ms, 128);
        ms.Length.Should().Be(2);
    }

    [Fact]
    public void SpanOverload_MatchesStreamOverload()
    {
        uint value = 300;
        using var ms = new MemoryStream();
        Leb128.Write(ms, value);
        var streamBytes = ms.ToArray();

        Span<byte> buf = stackalloc byte[8];
        Leb128.Write(buf, value, out var written);

        buf[..written].ToArray().Should().BeEquivalentTo(streamBytes);
    }

    [Fact]
    public void DeltaEncode_RoundTrip()
    {
        int[] values = [3, 7, 15, 100];
        using var ms = new MemoryStream();

        uint prev = 0;
        foreach (var v in values)
        {
            Leb128.Write(ms, (uint)v - prev);
            prev = (uint)v;
        }

        var bytes = ms.ToArray();
        var offset = 0;
        var decoded = new List<int>();
        uint running = 0;
        for (var i = 0; i < values.Length; i++)
        {
            running += Leb128.Read(bytes, ref offset);
            decoded.Add((int)running);
        }

        decoded.Should().BeEquivalentTo(values);
    }
}
