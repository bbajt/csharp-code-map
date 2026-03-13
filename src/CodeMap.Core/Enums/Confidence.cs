namespace CodeMap.Core.Enums;

/// <summary>
/// Confidence level of extracted symbol data.
/// Degrades when compilation fails or only syntactic analysis was possible.
/// </summary>
public enum Confidence
{
    /// <summary>Full semantic analysis succeeded; data is authoritative.</summary>
    High,

    /// <summary>Partial compilation; some cross-project types may be unresolved.</summary>
    Medium,

    /// <summary>Syntax-only extraction; semantic information is missing or inferred.</summary>
    Low,
}
