namespace CodeMap.Query.Tests;

using CodeMap.Query;
using FluentAssertions;

public class TokenSavingsTrackerTests
{
    private readonly TokenSavingsTracker _tracker = new();

    [Fact]
    public void TotalTokensSaved_InitiallyZero()
    {
        _tracker.TotalTokensSaved.Should().Be(0);
    }

    [Fact]
    public void TotalCostAvoided_InitiallyEmpty()
    {
        _tracker.TotalCostAvoided.Should().BeEmpty();
    }

    [Fact]
    public void RecordSaving_IncrementsTotalTokens()
    {
        _tracker.RecordSaving(500, new Dictionary<string, decimal>());
        _tracker.TotalTokensSaved.Should().Be(500);
    }

    [Fact]
    public void RecordSaving_MultipleCalls_Accumulates()
    {
        _tracker.RecordSaving(100, new Dictionary<string, decimal>());
        _tracker.RecordSaving(200, new Dictionary<string, decimal>());
        _tracker.RecordSaving(300, new Dictionary<string, decimal>());
        _tracker.TotalTokensSaved.Should().Be(600);
    }

    [Fact]
    public void RecordSaving_UpdatesCostPerModel()
    {
        _tracker.RecordSaving(1000, new Dictionary<string, decimal>
        {
            ["claude_sonnet"] = 0.003m,
            ["gpt4"] = 0.010m
        });

        _tracker.TotalCostAvoided["claude_sonnet"].Should().Be(0.003m);
        _tracker.TotalCostAvoided["gpt4"].Should().Be(0.010m);
    }

    [Fact]
    public void RecordSaving_MultipleCalls_AccumulatesCostPerModel()
    {
        _tracker.RecordSaving(1000, new Dictionary<string, decimal> { ["claude_sonnet"] = 0.003m });
        _tracker.RecordSaving(2000, new Dictionary<string, decimal> { ["claude_sonnet"] = 0.006m });

        _tracker.TotalCostAvoided["claude_sonnet"].Should().BeApproximately(0.009m, 0.0001m);
    }

    [Fact]
    public void RecordSaving_ConcurrentCalls_ThreadSafe()
    {
        var threads = Enumerable.Range(0, 50)
            .Select(_ => new Thread(() =>
                _tracker.RecordSaving(10, new Dictionary<string, decimal> { ["model"] = 0.001m })))
            .ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        _tracker.TotalTokensSaved.Should().Be(500);
        _tracker.TotalCostAvoided["model"].Should().BeApproximately(0.05m, 0.001m);
    }
}
