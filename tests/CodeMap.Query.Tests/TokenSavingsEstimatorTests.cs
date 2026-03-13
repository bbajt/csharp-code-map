namespace CodeMap.Query.Tests;

using CodeMap.Query;
using FluentAssertions;

public class TokenSavingsEstimatorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 750)]   // 800 - 50
    [InlineData(5, 3750)]  // 5 * 750
    [InlineData(20, 15000)] // 20 * 750
    public void ForSearch_ReturnsExpectedSavings(int hitCount, int expected)
    {
        TokenSavingsEstimator.ForSearch(hitCount).Should().Be(expected);
    }

    [Fact]
    public void ForCard_Returns1650()
    {
        TokenSavingsEstimator.ForCard().Should().Be(1650);
    }

    [Theory]
    [InlineData(100, 100, 0)]       // Same lines — no saving
    [InlineData(200, 50, 1500)]    // 200*10 - 50*10
    [InlineData(500, 10, 4900)]    // 500*10 - 10*10
    public void ForSpan_ReturnsExpectedSavings(int totalLines, int returnedLines, int expected)
    {
        TokenSavingsEstimator.ForSpan(totalLines, returnedLines).Should().Be(expected);
    }

    [Fact]
    public void ForSpan_WhenReturnedMoreThanTotal_ReturnsZero()
    {
        TokenSavingsEstimator.ForSpan(10, 50).Should().Be(0);
    }

    [Fact]
    public void EstimateCostAvoided_ReturnsPositiveForPositiveTokens()
    {
        var cost = TokenSavingsEstimator.EstimateCostAvoided(1000);
        cost.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateCostPerModel_ContainsExpectedModels()
    {
        var costs = TokenSavingsEstimator.EstimateCostPerModel(1000);
        costs.Should().ContainKey("claude_sonnet");
        costs.Should().ContainKey("claude_opus");
        costs.Should().ContainKey("gpt4");
    }

    [Fact]
    public void EstimateCostPerModel_ValuesArePositive()
    {
        var costs = TokenSavingsEstimator.EstimateCostPerModel(2000);
        costs.Values.Should().AllSatisfy(v => v.Should().BeGreaterThan(0));
    }
}
