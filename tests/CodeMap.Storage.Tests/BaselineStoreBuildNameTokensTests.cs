namespace CodeMap.Storage.Tests;

using FluentAssertions;

/// <summary>
/// Unit tests for BaselineStore.BuildNameTokens — the CamelCase FTS tokenizer helper.
/// </summary>
public class BaselineStoreBuildNameTokensTests
{
    [Theory]
    [InlineData("T:TestNs.IGitService",            "i git service")]
    [InlineData("T:TestNs.ISymbolStore",           "i symbol store")]
    [InlineData("M:TestNs.QueryEngine.SearchSymbolsAsync(System.String)", "search symbols async")]
    [InlineData("T:TestNs.OrderService",           "order service")]
    [InlineData("T:TestNs.HTMLParser",             "html parser")]
    [InlineData("T:TestNs.BaselineStore",          "baseline store")]
    [InlineData("T:TestNs.McpServer",              "mcp server")]
    [InlineData("P:TestNs.Foo.SomeProperty",       "some property")]
    [InlineData("",                                 "")]
    public void BuildNameTokens_VariousInputs_ProducesExpectedTokens(
        string fqn, string expectedTokens)
    {
        var tokens = BaselineStore.BuildNameTokens(fqn);
        tokens.Should().Be(expectedTokens);
    }

    [Fact]
    public void BuildNameTokens_GenericMethod_StripsArity()
    {
        var tokens = BaselineStore.BuildNameTokens("M:TestNs.Foo.GetItems``1(System.String)");
        tokens.Should().Be("get items");
    }

    [Fact]
    public void BuildNameTokens_NoNamespace_StillWorks()
    {
        var tokens = BaselineStore.BuildNameTokens("T:MyClass");
        tokens.Should().Be("my class");
    }
}
