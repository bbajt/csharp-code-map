namespace CodeMap.Core.Models;

/// <summary>
/// Per-project quality diagnostic from a compilation run.
/// Included in IndexStats and surfaced in ResponseMeta for agent observability.
/// </summary>
/// <remarks>
/// <para>
/// <b>Multi-target collapse (M20-01):</b> when a single <c>.csproj</c> targets
/// multiple frameworks (<c>net8.0;net9.0;net10.0</c>), Roslyn returns one
/// <c>Project</c> per TFM. CodeMap collapses these into one diagnostic — picking
/// the highest TFM as canonical for symbol / reference / fact extraction —
/// and lists every targeted framework in <see cref="TargetFrameworks"/>.
/// </para>
/// <para>
/// For single-target projects <see cref="TargetFrameworks"/> is <c>null</c>
/// (preserving the wire shape pre-M20-01).
/// </para>
/// </remarks>
public record ProjectDiagnostic(
    string ProjectName,
    bool Compiled,
    int SymbolCount,
    int ReferenceCount,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? TargetFrameworks = null
);
