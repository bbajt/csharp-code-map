namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using FluentAssertions;

/// <summary>
/// Pins <see cref="QueryEngine.BuildEndpoints"/> projection + filter behaviour.
/// Specifically verifies that the <c>http_method = "PAGE"</c> filter (added in
/// M19 for Blazor <c>@page</c> routes) returns only Blazor entries and excludes
/// HTTP methods like <c>GET</c>/<c>POST</c>.
/// </summary>
public sealed class BuildEndpointsFilterTests
{
    private static StoredFact Route(string value, string symbolId = "T:Test.Class") =>
        new(SymbolId: SymbolId.From(symbolId),
            StableId: null,
            Kind: FactKind.Route,
            Value: value,
            FilePath: FilePath.From("Test.cs"),
            LineStart: 1,
            LineEnd: 1,
            Confidence: Confidence.High);

    [Fact]
    public void NoFilter_ReturnsAllEndpoints()
    {
        var facts = new[]
        {
            Route("GET /api/orders"),
            Route("POST /api/orders"),
            Route("PAGE /counter"),
        };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: null);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void PageFilter_ReturnsBlazorRoutesOnly()
    {
        var facts = new[]
        {
            Route("GET /api/orders"),
            Route("POST /api/orders"),
            Route("PAGE /counter"),
            Route("PAGE /weather"),
            Route("DELETE /api/orders/{id}"),
        };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: "PAGE");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(e => e.HttpMethod.Should().Be("PAGE"));
        result.Select(e => e.RoutePath).Should().BeEquivalentTo(["/counter", "/weather"]);
    }

    [Fact]
    public void PageFilter_CaseInsensitive()
    {
        var facts = new[] { Route("PAGE /home") };

        var resultUpper = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: "PAGE");
        var resultLower = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: "page");

        resultUpper.Should().HaveCount(1);
        resultLower.Should().HaveCount(1);
    }

    [Fact]
    public void GetFilter_ExcludesPageRoutes()
    {
        var facts = new[]
        {
            Route("GET /api/orders"),
            Route("PAGE /counter"),
        };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: "GET");

        result.Should().ContainSingle();
        result[0].HttpMethod.Should().Be("GET");
        result[0].RoutePath.Should().Be("/api/orders");
    }

    [Fact]
    public void PathFilter_MatchesPrefix()
    {
        var facts = new[]
        {
            Route("PAGE /counter"),
            Route("PAGE /counters/active"),
            Route("PAGE /weather"),
        };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: "/counter", httpMethod: "PAGE");

        result.Should().HaveCount(2);
        result.Select(e => e.RoutePath).Should().BeEquivalentTo(["/counter", "/counters/active"]);
    }

    [Fact]
    public void PathPlusMethodFilter_BothApplied()
    {
        var facts = new[]
        {
            Route("GET /api/orders"),
            Route("POST /api/orders"),
            Route("PAGE /api/orders"),
        };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: "/api", httpMethod: "POST");

        result.Should().ContainSingle();
        result[0].HttpMethod.Should().Be("POST");
    }

    [Fact]
    public void RouteWithConstraints_PreservedThroughFilter()
    {
        var facts = new[] { Route("PAGE /items/{id:int}") };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: "PAGE");

        result.Should().ContainSingle();
        result[0].RoutePath.Should().Be("/items/{id:int}");
    }

    [Fact]
    public void MalformedFactValue_NoSpace_Skipped()
    {
        // Defensive: a fact value missing the method/path separator is skipped,
        // not crashed.
        var facts = new[]
        {
            Route("malformed-no-space"),
            Route("PAGE /ok"),
        };

        var result = QueryEngine.BuildEndpoints(facts, pathFilter: null, httpMethod: null);

        result.Should().ContainSingle();
        result[0].RoutePath.Should().Be("/ok");
    }
}
