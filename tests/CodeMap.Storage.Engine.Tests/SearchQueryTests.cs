namespace CodeMap.Storage.Engine.Tests;

using FluentAssertions;
using Xunit;

public sealed class SearchQueryTests
{
    private static IReadOnlyList<string> Raw(string query) =>
        SearchQuery.Parse(query).Groups.Select(g => g.RawText).ToList();

    [Fact]
    public void Parse_OrBetweenTerms_TwoGroups()
    {
        Raw("Foo OR Bar").Should().Equal("foo", "bar");
    }

    [Fact]
    public void Parse_OrHasLowestPrecedence()
    {
        Raw("Order Service OR Payment").Should().Equal("order service", "payment");
    }

    [Fact]
    public void Parse_LowercaseOr_IsATerm()
    {
        var groups = SearchQuery.Parse("foo or bar").Groups;

        groups.Should().ContainSingle();
        groups[0].Tokens.Should().Contain(["foo", "or", "bar"]);
    }

    [Fact]
    public void Parse_ExplicitAnd_Dropped()
    {
        SearchQuery.Parse("Foo AND Bar").Groups.Should().ContainSingle()
            .Which.Tokens.Should().BeEquivalentTo(["foo", "bar"]);
    }

    [Fact]
    public void Parse_QuotedPhrase_QuotesStrippedTokensAnded()
    {
        var group = SearchQuery.Parse("\"Order Service\"").Groups.Should().ContainSingle().Subject;

        group.RawText.Should().Be("order service");
        group.Tokens.Should().NotContain(t => t.Contains('"'));
        group.Tokens.Should().Contain(["order", "service"]);
    }

    [Fact]
    public void Parse_TrailingStarPerGroup_Stripped()
    {
        Raw("Refresh* OR Compute*").Should().Equal("refresh", "compute");
    }

    [Fact]
    public void Parse_Parentheses_Ignored()
    {
        // No nested grammar: (A OR B) C parses as A OR (B AND C).
        Raw("(Foo OR Bar) Baz").Should().Equal("foo", "bar baz");
    }

    [Theory]
    [InlineData("OR")]
    [InlineData("AND OR")]
    [InlineData("  ")]
    [InlineData("*")]
    [InlineData("\"\"")]
    public void Parse_OnlyOperatorsOrPunctuation_NoGroups(string query)
    {
        SearchQuery.Parse(query).Groups.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleTerm_SameTokensAsBefore()
    {
        // The pre-PHASE-21-08 normalizer: lowercase, split on separators, index tokenizer per segment.
        SearchQuery.Parse("MyApp.OrderService").Groups.Should().ContainSingle()
            .Which.Tokens.Should().BeEquivalentTo(["myapp", "orderservice"]);
    }
}
