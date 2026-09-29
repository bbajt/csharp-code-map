namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;

/// <summary>
/// The single rule that turns per-project outcomes into a run-level <see cref="SemanticLevel"/>.
/// Used by the build (<c>RoslynCompiler</c>) and by every reader of a stored baseline, so a
/// degradation the build detected is reported the same way on every query (F13, PHASE-21-07).
/// </summary>
public static class SemanticLevels
{
    /// <summary>
    /// True when the project's semantics are complete: it compiled and wasn't degraded by its
    /// environment (<see cref="IsEnvironmentDegraded"/>): every source generator loaded (F10, ADR-046)
    /// and it wasn't compiled with unresolved references for lack of a restore (F12, ADR-054).
    /// </summary>
    public static bool IsComplete(ProjectDiagnostic project) =>
        project.Compiled && !IsEnvironmentDegraded(project);

    /// <summary>
    /// True when the project's index is incomplete because of the machine that built it rather than the
    /// code at that commit: a source generator failed to load
    /// (<see cref="ProjectDiagnostic.GeneratorLoadFailures"/>), or the project has no restore output
    /// (<see cref="ProjectDiagnostic.MissingRestoreOutput"/>) <em>and</em> compile errors. A project
    /// that compiles cleanly without restore output (no packages, framework shipped with the SDK) is
    /// complete. Such baselines must not be shared with other machines (ADR-053, ADR-054).
    /// </summary>
    public static bool IsEnvironmentDegraded(ProjectDiagnostic project) =>
        project.GeneratorLoadFailures is { Count: > 0 }
        || (project.MissingRestoreOutput is not null && project.Errors is { Count: > 0 });

    /// <summary>
    /// <see cref="SemanticLevel.Full"/> when every project <see cref="IsComplete"/> (including an
    /// empty list); <see cref="SemanticLevel.SyntaxOnly"/> when none compiled; otherwise
    /// <see cref="SemanticLevel.Partial"/>.
    /// </summary>
    public static SemanticLevel Compute(IReadOnlyList<ProjectDiagnostic> projects)
    {
        int compiled = projects.Count(d => d.Compiled);
        int complete = projects.Count(IsComplete);
        return (compiled, complete, projects.Count) switch
        {
            (_, var ok, var total) when ok == total => SemanticLevel.Full,
            (0, _, _) => SemanticLevel.SyntaxOnly,
            _ => SemanticLevel.Partial,
        };
    }
}
