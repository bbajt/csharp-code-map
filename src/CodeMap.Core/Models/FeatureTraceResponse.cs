namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;

/// <summary>Response payload for graph.trace_feature.</summary>
public record FeatureTraceResponse(
    SymbolId EntryPoint,
    string EntryPointName,
    string? EntryPointRoute,
    IReadOnlyList<TraceNode> Nodes,
    int TotalNodesTraversed,
    int Depth,
    bool Truncated
);

/// <summary>A node in the feature trace tree with its callees and architectural annotations.</summary>
public record TraceNode(
    SymbolId SymbolId,
    StableId? StableId,
    string DisplayName,
    int Depth,
    IReadOnlyList<TraceAnnotation> Annotations,
    IReadOnlyList<TraceNode> Children
);

/// <summary>An architectural fact annotated on a trace node.</summary>
public record TraceAnnotation(
    string Kind,
    string Value,
    Confidence Confidence
);
