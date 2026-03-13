namespace CodeMap.Core.Models;

/// <summary>
/// Standard response wrapper for all MCP tool responses.
/// Every tool returns this type so agents get a consistent structure.
/// </summary>
/// <typeparam name="T">The tool-specific data payload type.</typeparam>
public record ResponseEnvelope<T>(
    string Answer,
    T Data,
    IReadOnlyList<EvidencePointer> Evidence,
    IReadOnlyList<NextAction> NextActions,
    Enums.Confidence Confidence,
    ResponseMeta Meta
);
