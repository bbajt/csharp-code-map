namespace CodeMap.Core.Models;

/// <summary>
/// A suggested follow-up MCP tool call, included in response envelopes
/// to guide agent navigation.
/// </summary>
public record NextAction(
    string Tool,
    string Rationale,
    Dictionary<string, object>? Parameters = null
);
