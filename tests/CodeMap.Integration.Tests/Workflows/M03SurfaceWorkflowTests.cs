namespace CodeMap.Integration.Tests.Workflows;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using FluentAssertions;

/// <summary>
/// Cross-cutting M03 surface workflow tests (PHASE-03-09 T01).
/// Each test chains 3+ tools to simulate realistic agent patterns.
/// Uses real Roslyn-indexed SampleSolution via IndexedSampleSolutionFixture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class M03SurfaceWorkflowTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public M03SurfaceWorkflowTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    // ── Workflow 1: "Map the API surface" ─────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_MapApiSurface()
    {
        // 1. surfaces.list_endpoints → get all endpoints
        var endpointsResult = await _f.QueryEngine.ListEndpointsAsync(
            Routing, pathFilter: null, httpMethod: null, limit: 50);
        endpointsResult.IsSuccess.Should().BeTrue();
        var endpoints = endpointsResult.Value.Data.Endpoints;
        endpoints.Should().NotBeEmpty("SampleApp.Api has an OrdersController with HTTP endpoints");

        // 2. Pick an endpoint's HandlerSymbol
        var handlerSymbol = endpoints[0].HandlerSymbol;

        // 3. symbols.get_card → verify card has Route fact
        var cardResult = await _f.QueryEngine.GetSymbolCardAsync(Routing, handlerSymbol);
        cardResult.IsSuccess.Should().BeTrue();
        var card = cardResult.Value.Data;
        card.Facts.Should().Contain(f => f.Kind == FactKind.Route,
            "handler method should have a Route fact");

        // 4. refs.find → see who references this endpoint handler
        var refsResult = await _f.QueryEngine.FindReferencesAsync(
            Routing, handlerSymbol, null, new BudgetLimits(maxResults: 20));
        refsResult.IsSuccess.Should().BeTrue();

        // Assert: all steps succeed and data is consistent
        card.SymbolId.Should().Be(handlerSymbol);
        endpoints[0].HttpMethod.Should().NotBeNullOrEmpty();
    }

    // ── Workflow 2: "Audit config usage" ──────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_AuditConfigUsage()
    {
        // 1. surfaces.list_config_keys → get all config keys
        var keysResult = await _f.QueryEngine.ListConfigKeysAsync(
            Routing, keyFilter: null, limit: 50);
        keysResult.IsSuccess.Should().BeTrue();
        var keys = keysResult.Value.Data.Keys;
        keys.Should().NotBeEmpty("ConfigConsumer.cs in SampleApp.Api accesses IConfiguration keys");

        // 2. Pick the UsedBySymbol from the first key
        var usedBySymbol = keys[0].UsedBySymbol;

        // 3. symbols.get_card → verify card has Config fact
        var cardResult = await _f.QueryEngine.GetSymbolCardAsync(Routing, usedBySymbol);
        cardResult.IsSuccess.Should().BeTrue();
        var card = cardResult.Value.Data;
        card.Facts.Should().Contain(f => f.Kind == FactKind.Config,
            "method that accesses config should have a Config fact on its card");

        // 4. symbols.get_definition_span → read the source code
        var spanResult = await _f.QueryEngine.GetDefinitionSpanAsync(
            Routing, usedBySymbol, 120, 2);
        spanResult.IsSuccess.Should().BeTrue();
        spanResult.Value.Data.Content.Should().NotBeNullOrEmpty();

        // Assert: config key visible on card, source available
        card.SymbolId.Should().Be(usedBySymbol);
        keys[0].Key.Should().NotBeNullOrEmpty();
    }

    // ── Workflow 3: "Understand data layer" ───────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_UnderstandDataLayer()
    {
        // 1. surfaces.list_db_tables → get all tables
        var tablesResult = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 50);
        tablesResult.IsSuccess.Should().BeTrue();
        var tables = tablesResult.Value.Data.Tables;
        tables.Should().NotBeEmpty("AppDbContext has DbSet<Order> entries");

        // 2. Note: DbTableInfo.EntitySymbol is the DbSet property's SymbolId, not
        //    the entity class. Use the fixture's known Order entity class for steps 3-4.
        var orderTableEntry = tables.FirstOrDefault(t =>
            t.TableName.Contains("Order", StringComparison.OrdinalIgnoreCase));
        orderTableEntry.Should().NotBeNull("AppDbContext.Orders DbSet must produce a DB table entry");

        // 3. types.hierarchy(entityClass) → check Order inherits from AuditableEntity
        var hierarchyResult = await _f.QueryEngine.GetTypeHierarchyAsync(
            Routing, _f.OrderId);
        hierarchyResult.IsSuccess.Should().BeTrue();
        hierarchyResult.Value.Data.BaseType.Should().NotBeNull(
            "Order extends AuditableEntity");

        // 4. refs.find(entityClass) → where is the Order entity used?
        var refsResult = await _f.QueryEngine.FindReferencesAsync(
            Routing, _f.OrderId, null,
            new BudgetLimits(maxResults: 20));
        refsResult.IsSuccess.Should().BeTrue();

        // Assert: table → entity → hierarchy → references all connected
        orderTableEntry!.TableName.Should().NotBeNullOrEmpty();
        hierarchyResult.Value.Data.Should().NotBeNull();
    }

    // ── Workflow 4: "DI audit" ────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_DiAudit()
    {
        // 1. symbols.search("ConfigureServices") — DI setup method
        var searchResult = await _f.QueryEngine.SearchSymbolsAsync(
            Routing, "ConfigureServices",
            new SymbolSearchFilters(Kinds: [SymbolKind.Method]),
            new BudgetLimits(maxResults: 5));
        searchResult.IsSuccess.Should().BeTrue();
        searchResult.Value.Data.Hits.Should().NotBeEmpty(
            "DiSetup.ConfigureServices exists in SampleApp.Api");

        var diMethodId = searchResult.Value.Data.Hits[0].SymbolId;

        // 2. symbols.get_card → verify DI registration facts
        var cardResult = await _f.QueryEngine.GetSymbolCardAsync(Routing, diMethodId);
        cardResult.IsSuccess.Should().BeTrue();
        var card = cardResult.Value.Data;
        card.Facts.Should().Contain(f => f.Kind == FactKind.DiRegistration,
            "ConfigureServices should have DI registration facts");

        // 3. For a registered service: refs.find → see usage across codebase
        var refsResult = await _f.QueryEngine.FindReferencesAsync(
            Routing, _f.IOrderServiceId, null, new BudgetLimits(maxResults: 20));
        refsResult.IsSuccess.Should().BeTrue();

        // Assert: DI facts visible, registered services traceable
        card.Facts.Where(f => f.Kind == FactKind.DiRegistration).Should().NotBeEmpty();
    }

    // ── Workflow 5: "Full stack trace" ────────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_FullStackTrace()
    {
        // 1. surfaces.list_endpoints → pick first endpoint
        var endpointsResult = await _f.QueryEngine.ListEndpointsAsync(
            Routing, pathFilter: null, httpMethod: null, limit: 10);
        endpointsResult.IsSuccess.Should().BeTrue();
        endpointsResult.Value.Data.Endpoints.Should().NotBeEmpty();

        var handlerSymbol = endpointsResult.Value.Data.Endpoints[0].HandlerSymbol;

        // 2. graph.callees(handlerMethod, depth: 2) → call tree
        var calleesResult = await _f.QueryEngine.GetCalleesAsync(
            Routing, handlerSymbol, depth: 2, limitPerLevel: 20, budgets: null);
        calleesResult.IsSuccess.Should().BeTrue();

        // 3. surfaces.list_db_tables → check both surface tools work in the same agent session
        var tablesResult = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 10);
        tablesResult.IsSuccess.Should().BeTrue();

        // Assert: can use endpoint info, call graph, and DB surface in one workflow
        endpointsResult.Value.Data.TotalCount.Should().BeGreaterThan(0);
        tablesResult.Value.Data.TotalCount.Should().BeGreaterThanOrEqualTo(0);
    }
}
