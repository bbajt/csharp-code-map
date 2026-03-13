namespace CodeMap.Query;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Assembles ResponseEnvelope&lt;T&gt; with consistent ResponseMeta population.
/// </summary>
public static class EnvelopeBuilder
{
    /// <summary>
    /// Assembles a <see cref="Core.Models.ResponseEnvelope{T}"/> with a fully populated
    /// <see cref="Core.Models.ResponseMeta"/> including timing, token savings, semantic level,
    /// workspace identity, and limits applied.
    /// </summary>
    public static ResponseEnvelope<T> Build<T>(
        T data,
        string answer,
        IReadOnlyList<EvidencePointer> evidence,
        IReadOnlyList<NextAction> nextActions,
        Confidence confidence,
        TimingBreakdown timing,
        IReadOnlyDictionary<string, LimitApplied> limitsApplied,
        CommitSha commitSha,
        long tokensSaved,
        decimal costAvoided,
        WorkspaceId? workspaceId = null,
        int overlayRevision = 0,
        SemanticLevel? semanticLevel = null,
        IReadOnlyList<ProjectDiagnostic>? projectDiagnostics = null)
    {
        var meta = new ResponseMeta(
            Timing: timing,
            BaselineCommitSha: commitSha,
            LimitsApplied: limitsApplied,
            TokensSaved: tokensSaved,
            CostAvoided: costAvoided,
            WorkspaceId: workspaceId,
            OverlayRevision: overlayRevision,
            SemanticLevel: semanticLevel,
            ProjectDiagnostics: projectDiagnostics);

        return new ResponseEnvelope<T>(answer, data, evidence, nextActions, confidence, meta);
    }
}
