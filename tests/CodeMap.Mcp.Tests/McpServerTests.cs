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

    // ── PHASE-21-09 T01: canonical names + dotted aliases (ADR-051) ─────────

    [Fact]
    public async Task ToolsCall_DottedAlias_DispatchesAndAppendsDeprecationNote()
    {
        var server = new McpServer(AliasedRegistry(), NullLogger<McpServer>.Instance);

        var canonical = (await RunAsync(server, ToolCall(1, "symbols_search"))).Single();
        var aliased = (await RunAsync(server, ToolCall(2, "symbols.search"))).Single();

        var content = aliased["result"]!["content"]!.AsArray();
        content.Should().HaveCount(2);
        content[0]!["text"]!.GetValue<string>().Should().Be(
            canonical["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var note = content[1]!["text"]!.GetValue<string>();
        note.Should().Contain("'symbols.search' is deprecated").And.Contain("'symbols_search'").And.Contain("2.11.0");
        aliased["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ToolsCall_CanonicalName_NoDeprecationNote()
    {
        var server = new McpServer(AliasedRegistry(), NullLogger<McpServer>.Instance);

        var response = (await RunAsync(server, ToolCall(1, "symbols_search"))).Single();

        response["error"].Should().BeNull();
        response["result"]!["content"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task ToolsCall_Alias_LogsWarningOncePerAlias()
    {
        var logger = new CapturingLogger();
        var server = new McpServer(AliasedRegistry(), logger);

        await RunAsync(server,
            ToolCall(1, "symbols.search"), ToolCall(2, "symbols.search"), ToolCall(3, "graph.callers"));

        logger.Warnings.Should().HaveCount(2);
        logger.Warnings.Should().ContainSingle(w => w.Contains("symbols.search") && w.Contains("symbols_search"));
        logger.Warnings.Should().ContainSingle(w => w.Contains("graph.callers") && w.Contains("graph_callers"));
    }

    [Theory]
    [InlineData("symbol.search", "symbols_search")]
    [InlineData("graph.caller", "graph_callers")]
    [InlineData("symbols_serch", "symbols_search")]
    public async Task ToolsCall_UnknownName_SuggestsCanonical(string requested, string expected)
    {
        var server = new McpServer(AliasedRegistry(), NullLogger<McpServer>.Instance);

        var response = (await RunAsync(server, ToolCall(1, requested))).Single();

        response["error"]!["message"]!.GetValue<string>().Should().EndWith($"Did you mean: {expected}?");
    }

    [Fact]
    public async Task ToolsCall_AliasHandlerThrows_ClassifiedWithCanonicalName()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(
            "test_throws", "Throws for tests", new JsonObject { ["type"] = "object" },
            (_, _) => Task.FromException<ToolCallResult>(new ArgumentException("bad"))));
        registry.RegisterAlias("test.throws", "test_throws");
        var server = new McpServer(registry, NullLogger<McpServer>.Instance);

        var response = (await RunAsync(server, ToolCall(1, "test.throws"))).Single();

        var envelope = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        envelope["code"]!.GetValue<string>().Should().Be("INTERNAL_ERROR");
        envelope["message"]!.GetValue<string>().Should().StartWith("test_throws failed");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ToolRegistry AliasedRegistry()
    {
        var registry = new ToolRegistry();
        foreach (var name in new[] { "symbols_search", "graph_callers" })
        {
            var n = name;
            registry.Register(new ToolDefinition(
                n, n, new JsonObject { ["type"] = "object" },
                (_, _) => Task.FromResult(new ToolCallResult($"{{\"tool\":\"{n}\"}}"))));
        }
        registry.RegisterAlias("symbols.search", "symbols_search");
        registry.RegisterAlias("graph.callers", "graph_callers");
        return registry;
    }

    private static JsonObject ToolCall(int id, string name) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = new JsonObject() },
    };

    private static async Task<List<JsonObject>> RunAsync(McpServer server, params JsonObject[] requests)
    {
        var text = string.Concat(requests.Select(r => r.ToJsonString() + "\n"));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var output = new MemoryStream();

        await server.RunAsync(input, output, CancellationToken.None);

        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .ToList();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<McpServer>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

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
