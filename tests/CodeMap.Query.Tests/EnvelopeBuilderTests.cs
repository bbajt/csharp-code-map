namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using FluentAssertions;

public class EnvelopeBuilderTests
{
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));

    [Fact]
    public void Build_SetsAnswerCorrectly()
    {
        var envelope = Build("The answer");
        envelope.Answer.Should().Be("The answer");
    }

    [Fact]
    public void Build_SetsDataCorrectly()
    {
        var envelope = Build("x");
        envelope.Data.Should().Be(42);
    }

    [Fact]
    public void Build_MetaContainsCommitSha()
    {
        var envelope = Build("x");
        envelope.Meta.BaselineCommitSha.Should().Be(Sha);
    }

    [Fact]
    public void Build_MetaContainsTokensSaved()
    {
        var envelope = Build("x", tokensSaved: 500);
        envelope.Meta.TokensSaved.Should().Be(500);
    }

    [Fact]
    public void Build_MetaContainsCostAvoided()
    {
        var envelope = Build("x", costAvoided: 0.5m);
        envelope.Meta.CostAvoided.Should().Be(0.5m);
    }

    [Fact]
    public void Build_MetaContainsTiming()
    {
        var timing = new TimingBreakdown(TotalMs: 42.5);
        var envelope = Build("x", timing: timing);
        envelope.Meta.Timing.TotalMs.Should().Be(42.5);
    }

    [Fact]
    public void Build_MetaLimitsApplied_Empty_WhenNoLimits()
    {
        var envelope = Build("x");
        envelope.Meta.LimitsApplied.Should().BeEmpty();
    }

    [Fact]
    public void Build_MetaLimitsApplied_ContainsAppliedLimits()
    {
        var limits = new Dictionary<string, LimitApplied>
        {
            ["MaxResults"] = new LimitApplied(200, 100)
        };
        var envelope = Build("x", limitsApplied: limits);
        envelope.Meta.LimitsApplied.Should().ContainKey("MaxResults");
    }

    [Fact]
    public void Build_ConfidenceIsPreserved()
    {
        var envelope = Build("x", confidence: Confidence.Low);
        envelope.Confidence.Should().Be(Confidence.Low);
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static ResponseEnvelope<int> Build(
        string answer,
        long tokensSaved = 0,
        decimal costAvoided = 0m,
        TimingBreakdown? timing = null,
        IReadOnlyDictionary<string, LimitApplied>? limitsApplied = null,
        Confidence confidence = Confidence.High)
    {
        return EnvelopeBuilder.Build(
            data: 42,
            answer: answer,
            evidence: [],
            nextActions: [],
            confidence: confidence,
            timing: timing ?? new TimingBreakdown(TotalMs: 1),
            limitsApplied: limitsApplied ?? new Dictionary<string, LimitApplied>(),
            commitSha: Sha,
            tokensSaved: tokensSaved,
            costAvoided: costAvoided);
    }
}
