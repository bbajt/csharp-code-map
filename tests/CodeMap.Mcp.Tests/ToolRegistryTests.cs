namespace CodeMap.Mcp.Tests;

using System.Text.Json.Nodes;
using FluentAssertions;

/// <summary>Unit tests for <see cref="ToolRegistry"/> aliases (PHASE-21-09 T01, ADR-051).</summary>
public sealed class ToolRegistryTests
{
    private static ToolRegistry RegistryWithSearch()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(
            "symbols_search", "search", new JsonObject(),
            (_, _) => Task.FromResult(new ToolCallResult("{}"))));
        return registry;
    }

    [Fact]
    public void Find_Alias_ReturnsCanonicalToolAndReportsAlias()
    {
        var registry = RegistryWithSearch();
        registry.RegisterAlias("symbols.search", "symbols_search");

        var tool = registry.Find("symbols.search", out var usedAlias);

        tool!.Name.Should().Be("symbols_search");
        usedAlias.Should().Be("symbols.search");
    }

    [Fact]
    public void Find_Canonical_NoAlias()
    {
        var registry = RegistryWithSearch();
        registry.RegisterAlias("symbols.search", "symbols_search");

        var tool = registry.Find("symbols_search", out var usedAlias);

        tool!.Name.Should().Be("symbols_search");
        usedAlias.Should().BeNull();
    }

    [Fact]
    public void Find_Unknown_ReturnsNull()
    {
        RegistryWithSearch().Find("symbols.search", out var usedAlias).Should().BeNull();
        usedAlias.Should().BeNull();
    }

    [Fact]
    public void RegisterAlias_UnknownCanonical_Throws()
    {
        var act = () => new ToolRegistry().RegisterAlias("symbols.search", "symbols_search");

        act.Should().Throw<InvalidOperationException>().WithMessage("*symbols_search*");
    }

    [Fact]
    public void Count_ExcludesAliases()
    {
        var registry = RegistryWithSearch();
        registry.RegisterAlias("symbols.search", "symbols_search");

        registry.Count.Should().Be(1);
        registry.GetAll().Select(t => t.Name).Should().Equal("symbols_search");
        registry.Aliases.Should().ContainKey("symbols.search");
    }
}
