namespace CodeMap.Core.Enums;

/// <summary>
/// Describes the overall quality of a compiled index.
/// Derived from per-project compilation outcomes in a single indexing run.
/// </summary>
public enum SemanticLevel
{
    /// <summary>All projects compiled successfully. All symbols and references are fully resolved.</summary>
    Full,

    /// <summary>
    /// Some projects compiled, others fell back to syntax-only extraction.
    /// Symbols from failed projects have Confidence.Low; references may be missing for those projects.
    /// </summary>
    Partial,

    /// <summary>
    /// No projects compiled. All data was extracted from syntax trees only.
    /// All symbols have Confidence.Low; no resolved reference edges.
    /// </summary>
    SyntaxOnly
}
