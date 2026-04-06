namespace CodeMap.Mcp;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hand-rolled MCP server using the stdio transport with Content-Length framing.
/// Protocol: JSON-RPC 2.0 over stdin/stdout, Content-Length header per message.
///
/// Handles:
///   initialize               → server capabilities
///   tools/list               → registered tool schemas
///   tools/call               → dispatches to ToolRegistry handlers
///   notifications/* (no id) → silently ignored
///   unknown methods         → JSON-RPC -32601 Method not found
/// </summary>
public sealed class McpServer
{
    private const string ProtocolVersionMin = "2024-11-05";
    private const string ProtocolVersionMax = "2025-03-26";

    /// <summary>Maximum allowed Content-Length for a single MCP message (10 MB).</summary>
    internal const int MaxContentLength = 10 * 1024 * 1024;

    private readonly ToolRegistry _registry;
    private readonly ILogger<McpServer> _logger;
    private readonly string _version;

    public McpServer(ToolRegistry registry, ILogger<McpServer> logger, string? version = null)
    {
        _registry = registry;
        _logger = logger;
        _version = version
            ?? Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "1.0.0";
    }

    /// <summary>Runs the server loop, reading from <paramref name="input"/> and writing to <paramref name="output"/>.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        // BOM-free UTF-8; leaveOpen so callers own the stream lifetime
        using var reader = new StreamReader(input, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true)
        { AutoFlush = false };
        await using (writer)
        {
            // Auto-detect transport format from first message.
            // Newer clients (protocol 2025-11-25+) use newline-delimited JSON.
            // Older clients / test harnesses use Content-Length framing (LSP style).
            bool? newlineDelimited = null;

            while (!ct.IsCancellationRequested)
            {
                var (body, isNewline) = await ReadMessageAsync(reader, newlineDelimited, ct).ConfigureAwait(false);
                if (body is null) break; // EOF — client disconnected

                newlineDelimited ??= isNewline; // latch on first detection
                _logger.LogDebug("MCP ← {Body}", body);

                JsonObject? msg;
                try { msg = JsonNode.Parse(body) as JsonObject; }
                catch { await SendAsync(writer, BuildError(null, -32700, "Parse error"), newlineDelimited ?? false, ct); continue; }

                if (msg is null) continue;

                // Notifications have no "id" member — ignore, no response
                if (!msg.ContainsKey("id"))
                {
                    _logger.LogDebug("MCP notification: {Method}", msg["method"]?.GetValue<string>() ?? "?");
                    continue;
                }

                var id = msg["id"]?.DeepClone();
                var method = msg["method"]?.GetValue<string>() ?? "";
                var @params = msg["params"] as JsonObject;

                var response = await DispatchAsync(method, id, @params, ct).ConfigureAwait(false);
                _logger.LogDebug("MCP → {Json}", response.ToJsonString());
                await SendAsync(writer, response, newlineDelimited ?? false, ct).ConfigureAwait(false);
            }
        }
    }

    // ─── Dispatch ─────────────────────────────────────────────────────────────

    private async Task<JsonObject> DispatchAsync(
        string method, JsonNode? id, JsonObject? @params, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                "initialize" => HandleInitialize(id, @params),
                "tools/list" => HandleToolsList(id),
                "tools/call" => await HandleToolCallAsync(id, @params, ct).ConfigureAwait(false),
                _ => BuildError(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error dispatching {Method}", method);
            return BuildError(id, -32603, "Internal error");
        }
    }

    // ─── Method handlers ──────────────────────────────────────────────────────

    private JsonObject HandleInitialize(JsonNode? id, JsonObject? @params)
    {
        // Echo back the client's requested version if it's within our supported range,
        // so newer MCP clients (e.g. 2025-03-26) don't reject the connection.
        var requested = @params?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersionMin;
        var negotiated = string.Compare(requested, ProtocolVersionMax, StringComparison.Ordinal) <= 0
            ? requested
            : ProtocolVersionMax;

        return BuildSuccess(id, new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "codemap", ["version"] = _version },
        });
    }

    private JsonObject HandleToolsList(JsonNode? id)
    {
        var tools = new JsonArray();
        foreach (var t in _registry.GetAll())
        {
            tools.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            });
        }
        return BuildSuccess(id, new JsonObject { ["tools"] = tools });
    }

    private async Task<JsonObject> HandleToolCallAsync(
        JsonNode? id, JsonObject? @params, CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            return BuildError(id, -32602, "Invalid params: 'name' is required");

        var tool = _registry.Find(name);
        if (tool is null)
            return BuildError(id, -32602, $"Unknown tool: {name}");

        var arguments = @params?["arguments"] as JsonObject;
        var toolResult = await tool.Handler(arguments, ct).ConfigureAwait(false);

        return BuildSuccess(id, new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = toolResult.Content } },
            ["isError"] = toolResult.IsError,
        });
    }

    // ─── JSON-RPC helpers ─────────────────────────────────────────────────────

    private static JsonObject BuildSuccess(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject BuildError(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    // ─── Transport ────────────────────────────────────────────────────────────

    private static async Task SendAsync(StreamWriter writer, JsonObject response, bool newlineDelimited, CancellationToken ct)
    {
        var json = response.ToJsonString();
        if (newlineDelimited)
        {
            await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
        }
        else
        {
            var byteCount = Encoding.UTF8.GetByteCount(json);
            await writer.WriteAsync($"Content-Length: {byteCount}\r\n\r\n".AsMemory(), ct).ConfigureAwait(false);
            await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one message from the stream, auto-detecting the transport format.
    /// Returns (null, _) on EOF.
    /// <paramref name="knownFormat"/>: null = detect, true = newline-delimited, false = Content-Length framed.
    /// </summary>
    internal static async Task<(string? Body, bool IsNewline)> ReadMessageAsync(
        StreamReader reader, bool? knownFormat, CancellationToken ct)
    {
        var firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (firstLine is null) return (null, false); // EOF

        // Newline-delimited JSON: line starts with '{' (or detected from prior message)
        if (knownFormat == true || firstLine.StartsWith("{", StringComparison.Ordinal))
            return (firstLine, true);

        // Content-Length framed (LSP style): parse headers until blank line
        int contentLength = -1;
        if (firstLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(firstLine.AsSpan("Content-Length:".Length).Trim(), out var len))
            contentLength = len;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return (null, false); // EOF
            if (line.Length == 0) break;            // blank line ends headers

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var len2))
                contentLength = len2;
        }

        if (contentLength <= 0 || contentLength > MaxContentLength) return (null, false);

        var buffer = new char[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await reader.ReadAsync(
                buffer.AsMemory(totalRead, contentLength - totalRead), ct).ConfigureAwait(false);
            if (read == 0) return (null, false); // EOF mid-message
            totalRead += read;
        }

        return (new string(buffer, 0, totalRead), false);
    }
}
