namespace CodeMap.Core.Interfaces;

/// <summary>
/// Marks an exception as a storage-layer failure so outer layers (MCP handlers) can map it to
/// <c>STORAGE_ERROR</c> without referencing the storage engine's concrete exception types —
/// CodeMap.Mcp depends only on Core and Query (ADR-041).
/// </summary>
public interface IStorageFailure
{
    /// <summary>
    /// True when the failure is expected to clear on its own (e.g. another process holds the
    /// resource); callers should simply retry. False when an action such as rebuilding the
    /// baseline is needed first.
    /// </summary>
    bool IsTransient { get; }
}
