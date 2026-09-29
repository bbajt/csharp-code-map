namespace CodeMap.Query.Tests;

using FluentAssertions;

public class FtsQuerySanitizerTests
{
    [Theory]
    [InlineData("OrderService", "OrderService")]
    [InlineData("IOrderService", "IOrderService")]
    [InlineData("order*", "order*")]
    [InlineData("Foo OR Bar", "Foo OR Bar")]
    [InlineData("  trimmed  ", "trimmed")]
    public void Sanitize_NormalQuery_PassesThrough(string input, string expected) =>
        FtsQuerySanitizer.Sanitize(input).Should().Be(expected);

    [Theory]
    [InlineData("^unknown", "unknown")]
    [InlineData("^trigram", "trigram")]
    [InlineData("^^double", "double")]
    public void Sanitize_CaretPrefix_Stripped(string input, string expected) =>
        FtsQuerySanitizer.Sanitize(input).Should().Be(expected);

    // ── PHASE-21-08 T01 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Foo NOT Bar", "NOT")]
    [InlineData("Foo NEAR Bar", "NEAR")]
    [InlineData("NOT Foo", "NOT")]
    public void UnsupportedOperator_NotOrNear_Detected(string query, string expected) =>
        FtsQuerySanitizer.UnsupportedOperator(query).Should().Be(expected);

    [Theory]
    [InlineData("Foo OR Bar")]
    [InlineData("Foo AND Bar")]
    [InlineData("not Foo")]          // lowercase: a term, as in FTS5
    [InlineData("NotificationService")]
    [InlineData("\"Order Service\"")]
    public void UnsupportedOperator_SupportedSyntax_Null(string query) =>
        FtsQuerySanitizer.UnsupportedOperator(query).Should().BeNull();

    [Theory]
    [InlineData("OR")]
    [InlineData("AND OR")]
    [InlineData("( )")]
    [InlineData("\"\"")]
    public void Sanitize_OnlyOperatorsOrPunctuation_ReturnsNull(string query) =>
        FtsQuerySanitizer.Sanitize(query).Should().BeNull();

    [Fact]
    public void Sanitize_CaretOnly_ReturnsNull() =>
        FtsQuerySanitizer.Sanitize("^").Should().BeNull();

    [Fact]
    public void Sanitize_CaretWithSpaces_ReturnsNull() =>
        FtsQuerySanitizer.Sanitize("^  ").Should().BeNull();

    [Fact]
    public void Sanitize_UnbalancedOpenQuote_QuotesStripped() =>
        FtsQuerySanitizer.Sanitize("\"OrderService").Should().Be("OrderService");

    [Fact]
    public void Sanitize_BalancedQuotes_PassesThrough() =>
        FtsQuerySanitizer.Sanitize("\"Order Service\"").Should().Be("\"Order Service\"");

    [Fact]
    public void Sanitize_ThreeQuotes_QuotesStripped() =>
        FtsQuerySanitizer.Sanitize("\"foo\" \"bar").Should().Be("foo bar");

    [Fact]
    public void Sanitize_EmptyString_ReturnsNull() =>
        FtsQuerySanitizer.Sanitize("").Should().BeNull();

    [Fact]
    public void Sanitize_WhitespaceOnly_ReturnsNull() =>
        FtsQuerySanitizer.Sanitize("   ").Should().BeNull();
}
