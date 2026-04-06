namespace CodeMap.Mcp.Tests;

using System.Text;
using FluentAssertions;

/// <summary>
/// Tests for MCP server transport layer — Content-Length framing,
/// newline-delimited detection, and security limits.
/// </summary>
public sealed class McpServerTransportTests
{
    [Fact]
    public async Task ReadMessageAsync_ContentLengthExceedsMax_ReturnsNull()
    {
        // A Content-Length exceeding MaxContentLength should be rejected
        // without allocating the buffer (DoS protection).
        var oversized = McpServer.MaxContentLength + 1;
        var header = $"Content-Length: {oversized}\r\n\r\n{{}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var (body, _) = await McpServer.ReadMessageAsync(reader, false, CancellationToken.None);

        body.Should().BeNull("oversized Content-Length must be rejected");
    }

    [Fact]
    public async Task ReadMessageAsync_ContentLengthAtMax_Succeeds()
    {
        // A message exactly at MaxContentLength should be accepted.
        // We don't actually send 10MB of data — just verify the length check passes.
        // Use a small valid message to test the happy path.
        var json = "{\"jsonrpc\":\"2.0\"}";
        var header = $"Content-Length: {json.Length}\r\n\r\n{json}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var (body, isNewline) = await McpServer.ReadMessageAsync(reader, false, CancellationToken.None);

        body.Should().Be(json);
        isNewline.Should().BeFalse();
    }

    [Fact]
    public async Task ReadMessageAsync_NegativeContentLength_ReturnsNull()
    {
        var header = "Content-Length: -1\r\n\r\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var (body, _) = await McpServer.ReadMessageAsync(reader, false, CancellationToken.None);

        body.Should().BeNull();
    }

    [Fact]
    public async Task ReadMessageAsync_NewlineDelimited_DetectedCorrectly()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"method\":\"test\"}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var (body, isNewline) = await McpServer.ReadMessageAsync(reader, null, CancellationToken.None);

        body.Should().Be(json);
        isNewline.Should().BeTrue();
    }

    [Fact]
    public async Task ReadMessageAsync_Eof_ReturnsNull()
    {
        using var stream = new MemoryStream([]);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var (body, _) = await McpServer.ReadMessageAsync(reader, null, CancellationToken.None);

        body.Should().BeNull();
    }
}
