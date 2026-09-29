namespace CodeMap.Harness.Concurrency;

using System.Text.Json.Nodes;

/// <summary>
/// Transport-agnostic MCP client used by the concurrency driver. T1 (one stdio process per
/// agent) is implemented by <see cref="StdioMcpClient"/>; shared-host / HTTP / adapter
/// topologies (T2–T4) plug in behind the same interface later.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>OS process id of the daemon serving this client (memory sampling).</summary>
    int ProcessId { get; }

    /// <summary>Wall-clock time from process start to the <c>initialize</c> response.</summary>
    TimeSpan InitializeElapsed { get; }

    /// <summary>
    /// Calls one MCP tool. Never throws for tool, protocol or transport failures —
    /// they come back as a failed <see cref="McpCallResult"/> with an error code.
    /// Only <paramref name="ct"/> cancellation propagates.
    /// </summary>
    Task<McpCallResult> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct);
}

/// <summary>
/// Outcome of one tool call. <see cref="ErrorCode"/> is the CodeMap error code
/// (e.g. <c>INVALID_ARGUMENT</c>) or a synthetic transport code:
/// <c>INTERNAL</c> (JSON-RPC -32603), <c>RPC_&lt;n&gt;</c> (other JSON-RPC errors),
/// <c>TIMEOUT</c>, <c>TRANSPORT_EXIT</c>.
/// </summary>
public sealed record McpCallResult(
    string Tool,
    bool Ok,
    string? ErrorCode,
    string? ErrorMessage,
    TimeSpan Latency,
    string? Payload)
{
    /// <summary>Parses <see cref="Payload"/> as JSON; null when absent or not JSON.</summary>
    public JsonNode? PayloadJson()
    {
        if (string.IsNullOrEmpty(Payload)) return null;
        try { return JsonNode.Parse(Payload); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
