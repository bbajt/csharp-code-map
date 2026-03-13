namespace CodeMap.Core.Models;

/// <summary>
/// Metadata included in every ResponseEnvelope for observability and cost tracking.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CostAvoided"/> is a single <c>decimal</c> value using the claude_sonnet rate
/// (not a per-model dictionary — see ADR-013).
/// Per-model breakdown is available on <c>ITokenSavingsTracker.TotalCostAvoided</c>.
/// </para>
/// <para>
/// <see cref="SemanticLevel"/> reflects the quality of the baseline index:
/// Full = all projects compiled, Partial = mixed, SyntaxOnly = no projects compiled.
/// Null for baselines indexed before PHASE-02-08.
/// </para>
/// <para>
/// <see cref="WorkspaceId"/> and <see cref="OverlayRevision"/> are populated for
/// Workspace and Ephemeral mode responses (ADR-019). Null/0 for Committed mode.
/// </para>
/// </remarks>
public record ResponseMeta(
    TimingBreakdown Timing,
    Types.CommitSha BaselineCommitSha,
    IReadOnlyDictionary<string, LimitApplied> LimitsApplied,
    long TokensSaved,
    decimal CostAvoided,
    Types.WorkspaceId? WorkspaceId = null,
    int OverlayRevision = 0,
    Enums.SemanticLevel? SemanticLevel = null,
    IReadOnlyList<ProjectDiagnostic>? ProjectDiagnostics = null
);
