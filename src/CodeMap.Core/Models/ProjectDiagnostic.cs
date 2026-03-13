namespace CodeMap.Core.Models;

/// <summary>
/// Per-project quality diagnostic from a compilation run.
/// Included in IndexStats and surfaced in ResponseMeta for agent observability.
/// </summary>
public record ProjectDiagnostic(
    string ProjectName,
    bool Compiled,
    int SymbolCount,
    int ReferenceCount,
    IReadOnlyList<string>? Errors = null
);
