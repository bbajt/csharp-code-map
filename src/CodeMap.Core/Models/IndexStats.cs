namespace CodeMap.Core.Models;

/// <summary>
/// Statistics from a completed indexing operation.
/// </summary>
public record IndexStats(
    int SymbolCount,
    int ReferenceCount,
    int FileCount,
    double ElapsedSeconds,
    Enums.Confidence Confidence,
    Enums.SemanticLevel SemanticLevel = Enums.SemanticLevel.Full,
    IReadOnlyList<ProjectDiagnostic>? ProjectDiagnostics = null
);
