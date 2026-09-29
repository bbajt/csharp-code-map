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
/// <para>
/// <b>Generator load failures (F10, PHASE-21-05):</b> <see cref="GeneratorLoadFailures"/> lists every
/// source-generator assembly the compiler refused to load (typically one built for a newer Roslyn
/// than CodeMap bundles), e.g.
/// <c>"Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)"</c>.
/// Code those generators produce (e.g. Razor components) is missing from the index, and the run's
/// <see cref="Enums.SemanticLevel"/> is at most <c>Partial</c>. <c>null</c> when none (additive wire shape).
/// </para>
/// <para>
/// <b>Missing restore output (F12, PHASE-21-07):</b> <see cref="MissingRestoreOutput"/> is the
/// solution-relative path where <c>project.assets.json</c> was expected when the project uses NuGet
/// restore but was never restored (a fresh clone or git worktree: <c>obj/</c> is gitignored and
/// MSBuildWorkspace doesn't restore). Its references, sometimes including the target framework's, are
/// unresolved. Together with compile errors it makes the run at most <c>Partial</c>. <c>null</c> when
/// the restore output was found, or the project doesn't use NuGet restore.
/// </para>
/// </remarks>
public record ProjectDiagnostic(
    string ProjectName,
    bool Compiled,
    int SymbolCount,
    int ReferenceCount,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? TargetFrameworks = null,
    IReadOnlyList<string>? GeneratorLoadFailures = null,
    string? MissingRestoreOutput = null
);
