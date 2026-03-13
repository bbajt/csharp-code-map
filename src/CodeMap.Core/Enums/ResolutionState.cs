namespace CodeMap.Core.Enums;

/// <summary>
/// Indicates whether a reference edge was fully resolved via SemanticModel
/// or extracted syntactically (without compilation).
/// </summary>
public enum ResolutionState
{
    /// <summary>Full SemanticModel resolution — to_symbol_id is the exact target.</summary>
    Resolved,

    /// <summary>
    /// Syntactic extraction only — to_name and to_container_hint are populated;
    /// to_symbol_id is empty (""). Resolution workers may upgrade this later.
    /// </summary>
    Unresolved
}
