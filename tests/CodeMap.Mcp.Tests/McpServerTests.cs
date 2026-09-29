namespace CodeMap.Mcp.Tests;

using System.Text;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="McpServer"/> tool-call boundary (PHASE-21-04 T02, ADR-045): an exception escaping a
/// tool handler must come back as a CodeMap error envelope (<c>isError: true</c>, honest code,
/// <c>retryable</c>), never as JSON-RPC <c>-32603</c>. Protocol framing is covered by
/// <see cref="McpServerTransportTests"/>.
/// </summary>
public sealed class McpServerTests
{
    private const int WinSharingViolation = unchecked((int)0x80070020);

    private static IOException SharingViolation() =>
        new("The process cannot access the file 'overlay.wal' because it is being used by another process.",
            OperatingSystem.IsWindows() ? WinSharingViolation : OperatingSystem.IsMacOS() ? 35 : 11);

    [Fact]
    public async Task ToolThrowsSharingViolation_WithWorkspaceId_ReturnsWorkspaceInUseRetryable()
    {
        var (response, envelope) = await CallThrowingToolAsync(SharingViolation(), new JsonObject { ["workspace_id"] = "session" });

        response["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        envelope["code"]!.GetValue<string>().Should().Be("WORKSPACE_IN_USE");
        envelope["retryable"]!.GetValue<bool>().Should().BeTrue();
        envelope["details"]!["workspace_id"]!.GetValue<string>().Should().Be("session");
    }

    [Fact]
    public async Task ToolThrowsSharingViolation_NoWorkspace_ReturnsStorageErrorRetryable()
    {
        var (_, envelope) = await CallThrowingToolAsync(SharingViolation(), new JsonObject());

        envelope["code"]!.GetValue<string>().Should().Be("STORAGE_ERROR");
        envelope["retryable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ToolThrowsStorageFailure_ReturnsStorageErrorWithOriginalMessage()
    {
        var ex = new FakeStorageFailure(
            "Baseline for repo r commit c is incomplete (missing segments); run index.ensure_baseline to rebuild it.");

        var (_, envelope) = await CallThrowingToolAsync(ex, new JsonObject());

        envelope["code"]!.GetValue<string>().Should().Be("STORAGE_ERROR");
        envelope["message"]!.GetValue<string>().Should().Contain("run index.ensure_baseline");
    }

    [Fact]
    public async Task ToolThrowsArgumentException_ReturnsInternalErrorWithExceptionType()
    {
        // At the outer boundary an ArgumentException is a defect, not caller input (v2.6.2:
        // SymbolId.From("") on an overlay symbol) — handlers validate input themselves.
        var (_, envelope) = await CallThrowingToolAsync(new ArgumentException("Value cannot be empty."), new JsonObject());

        envelope["code"]!.GetValue<string>().Should().Be("INTERNAL_ERROR");
        envelope["retryable"]!.GetValue<bool>().Should().BeFalse();
        envelope["details"]!["exception_type"]!.GetValue<string>().Should().Be("System.ArgumentException");
    }

    [Fact]
    public async Task ToolThrowsUnexpected_ReturnsInternalError()
    {
        var (_, envelope) = await CallThrowingToolAsync(new InvalidOperationException("boom"), new JsonObject());

        envelope["code"]!.GetValue<string>().Should().Be("INTERNAL_ERROR");
        envelope["message"]!.GetValue<string>().Should().Contain("boom");
        envelope["details"]!["exception_type"]!.GetValue<string>().Should().Be("System.InvalidOperationException");
    }

    [Fact]
    public async Task ToolThrowsOperationCanceled_IsNotConvertedToToolError()
    {
        // Cancellation keeps propagating (server shutdown); it must not be reported as a tool failure.
        var act = async () => await CallThrowingToolAsync(new OperationCanceledException(), new JsonObject());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<(JsonObject Response, JsonObject Envelope)> CallThrowingToolAsync(
        Exception toThrow, JsonObject arguments)
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(
            "test.throws", "Throws for tests", new JsonObject { ["type"] = "object" },
            (_, _) => Task.FromException<ToolCallResult>(toThrow)));
        var server = new McpServer(registry, NullLogger<McpServer>.Instance);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "test.throws", ["arguments"] = arguments },
        };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"));
        using var output = new MemoryStream();

        await server.RunAsync(input, output, CancellationToken.None);

        var line = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        var response = JsonNode.Parse(line)!.AsObject();
        response["error"].Should().BeNull(
            $"a tool failure must be an error envelope, not JSON-RPC error {response["error"]?.ToJsonString()}");
        var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var envelope = text is null ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
        return (response, envelope);
    }

    private sealed class FakeStorageFailure(string message) : Exception(message), IStorageFailure
    {
        public bool IsTransient => false;
    }
}
