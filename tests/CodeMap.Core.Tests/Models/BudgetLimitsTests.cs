namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Models;
using FluentAssertions;

public sealed class BudgetLimitsTests
{
    [Fact]
    public void Defaults_AllFieldsMatchSpec()
    {
        BudgetLimits.Defaults.MaxResults.Should().Be(20);
        BudgetLimits.Defaults.MaxReferences.Should().Be(50);
        BudgetLimits.Defaults.MaxDepth.Should().Be(3);
        BudgetLimits.Defaults.MaxLines.Should().Be(120);
        BudgetLimits.Defaults.MaxChars.Should().Be(12_000);
    }

    [Fact]
    public void HardCaps_AllFieldsMatchSpec()
    {
        BudgetLimits.HardCaps.MaxResults.Should().Be(100);
        BudgetLimits.HardCaps.MaxReferences.Should().Be(500);
        BudgetLimits.HardCaps.MaxDepth.Should().Be(6);
        BudgetLimits.HardCaps.MaxLines.Should().Be(400);
        BudgetLimits.HardCaps.MaxChars.Should().Be(40_000);
    }

    [Fact]
    public void Constructor_ZeroMaxResults_Throws() =>
        FluentActions.Invoking(() => new BudgetLimits(0))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Constructor_NegativeMaxLines_Throws() =>
        FluentActions.Invoking(() => new BudgetLimits(maxLines: -1))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void ClampToHardCaps_AllUnderCap_ReturnsUnchanged()
    {
        var limits = new BudgetLimits(10, 30, 2, 50, 5_000);
        var (clamped, applied) = limits.ClampToHardCaps();

        clamped.MaxResults.Should().Be(10);
        clamped.MaxReferences.Should().Be(30);
        applied.Should().BeEmpty();
    }

    [Fact]
    public void ClampToHardCaps_ResultsOverCap_ClampsAndReports()
    {
        var limits = new BudgetLimits(200, 50, 3, 120, 12_000);
        var (clamped, applied) = limits.ClampToHardCaps();

        clamped.MaxResults.Should().Be(100);
        applied.Should().ContainKey("MaxResults");
        applied["MaxResults"].Requested.Should().Be(200);
        applied["MaxResults"].HardCap.Should().Be(100);
    }

    [Fact]
    public void ClampToHardCaps_MultipleCapsHit_AllReported()
    {
        var limits = new BudgetLimits(200, 600, 10, 500, 50_000);
        var (_, applied) = limits.ClampToHardCaps();

        applied.Should().ContainKey("MaxResults");
        applied.Should().ContainKey("MaxReferences");
        applied.Should().ContainKey("MaxDepth");
        applied.Should().ContainKey("MaxLines");
        applied.Should().ContainKey("MaxChars");
    }
}
