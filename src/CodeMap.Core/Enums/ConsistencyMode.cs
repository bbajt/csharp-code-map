namespace CodeMap.Core.Enums;

/// <summary>
/// Controls which data sources the query engine consults.
/// </summary>
public enum ConsistencyMode
{
    /// <summary>Query only the immutable baseline index for the current HEAD commit.</summary>
    Committed,

    /// <summary>Query baseline merged with the active workspace overlay for the given WorkspaceId.</summary>
    Workspace,

    /// <summary>Query an ephemeral (in-memory only) overlay for short-lived agent tasks.</summary>
    Ephemeral,
}
