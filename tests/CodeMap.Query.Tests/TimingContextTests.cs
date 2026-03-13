namespace CodeMap.Query.Tests;

using FluentAssertions;

public class TimingContextTests
{
    [Fact]
    public async Task Build_ReturnsNonNegativeTotal()
    {
        var tc = new TimingContext();
        await Task.Delay(10);
        var result = tc.Build();
        result.TotalMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task EndCacheLookup_RecordsCacheLookupMs()
    {
        var tc = new TimingContext();
        tc.StartPhase();
        await Task.Delay(10);
        tc.EndCacheLookup();
        var result = tc.Build();
        result.CacheLookupMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task EndDbQuery_RecordsDbQueryMs()
    {
        var tc = new TimingContext();
        tc.StartPhase();
        await Task.Delay(10);
        tc.EndDbQuery();
        var result = tc.Build();
        result.DbQueryMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task EndRanking_RecordsRankingMs()
    {
        var tc = new TimingContext();
        tc.StartPhase();
        await Task.Delay(10);
        tc.EndRanking();
        var result = tc.Build();
        result.RankingMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task EndRoslynCompile_RecordsRoslynCompileMs()
    {
        var tc = new TimingContext();
        tc.StartPhase();
        await Task.Delay(10);
        tc.EndRoslynCompile();
        var result = tc.Build();
        result.RoslynCompileMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task MultiplePhases_AccumulateCorrectly()
    {
        var tc = new TimingContext();

        tc.StartPhase();
        await Task.Delay(5);
        tc.EndCacheLookup();

        tc.StartPhase();
        await Task.Delay(5);
        tc.EndDbQuery();

        tc.StartPhase();
        await Task.Delay(5);
        tc.EndRanking();

        var result = tc.Build();
        result.CacheLookupMs.Should().BeGreaterThanOrEqualTo(0);
        result.DbQueryMs.Should().BeGreaterThanOrEqualTo(0);
        result.RankingMs.Should().BeGreaterThanOrEqualTo(0);
        result.TotalMs.Should().BeGreaterThanOrEqualTo(result.CacheLookupMs + result.DbQueryMs + result.RankingMs);
    }

    [Fact]
    public void NoPhases_AllZeroExceptTotal()
    {
        var tc = new TimingContext();
        var result = tc.Build();
        result.CacheLookupMs.Should().Be(0);
        result.DbQueryMs.Should().Be(0);
        result.RankingMs.Should().Be(0);
        result.RoslynCompileMs.Should().Be(0);
        result.TotalMs.Should().BeGreaterThanOrEqualTo(0);
    }
}
