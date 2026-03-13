namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Regression tests for DbTableExtractor multi-line SQL support (PHASE-07-02 GAP-01 fix).
/// Verifies that tables from multi-line verbatim strings are correctly extracted.
/// Uses RawSqlQueries.cs (SampleApp/Data) and two DbContext classes (OrderDbContext, AuditDbContext).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Regression")]
public sealed class DbTableExtractionTests
{
    private readonly IndexedSampleSolutionFixture _f;

    public DbTableExtractionTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private CodeMap.Core.Models.RoutingContext Routing => _f.CommittedRouting();

    // ── Multi-line SQL table extraction ───────────────────────────────────────

    [Fact]
    public async Task Regression_DbTable_MultiLineSql_SelectFrom()
    {
        var result = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var tableNames = result.Value.Data.Tables.Select(t => t.TableName).ToHashSet();

        tableNames.Should().Contain("Orders",
            "RawSqlQueries.GetOrdersByStatus has 'FROM Orders o' in multi-line SQL");
    }

    [Fact]
    public async Task Regression_DbTable_MultiLineSql_InsertInto()
    {
        var result = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var tableNames = result.Value.Data.Tables.Select(t => t.TableName).ToHashSet();

        tableNames.Should().Contain("AuditLog",
            "RawSqlQueries.InsertAuditLog has 'INSERT INTO AuditLog' in multi-line SQL");
    }

    [Fact]
    public async Task Regression_DbTable_MultiLineSql_Update()
    {
        var result = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var tableNames = result.Value.Data.Tables.Select(t => t.TableName).ToHashSet();

        tableNames.Should().Contain("Orders",
            "RawSqlQueries.UpdateOrderStatus has 'UPDATE Orders' in multi-line SQL");
    }

    [Fact]
    public async Task Regression_DbTable_MultiLineSql_InnerJoin()
    {
        var result = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var tableNames = result.Value.Data.Tables.Select(t => t.TableName).ToHashSet();

        tableNames.Should().Contain("Customers",
            "RawSqlQueries.GetOrdersByStatus has 'INNER JOIN Customers' in multi-line SQL");
    }

    [Fact]
    public async Task Regression_DbTable_SingleLine_StillWorks()
    {
        var result = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var tableNames = result.Value.Data.Tables.Select(t => t.TableName).ToHashSet();

        tableNames.Should().Contain("Orders",
            "RawSqlQueries.CountOrders has 'FROM Orders' in single-line SQL (regression)");
    }

    // ── Multiple DbContexts ───────────────────────────────────────────────────

    [Fact]
    public async Task Regression_DbTable_MultipleDbContexts_AllDbSetsFound()
    {
        var result = await _f.QueryEngine.ListDbTablesAsync(
            Routing, tableFilter: null, limit: 100);

        result.IsSuccess.Should().BeTrue();
        var tableNames = result.Value.Data.Tables.Select(t => t.TableName).ToHashSet();

        // OrderDbContext has DbSet<Order> Orders + DbSet<Customer> Customers (table name = property name)
        tableNames.Should().Contain("Orders",
            "OrderDbContext has DbSet<Order> Orders property — table name = property name");
        tableNames.Should().Contain("Customers",
            "OrderDbContext has DbSet<Customer> Customers property — table name = property name");

        // AuditDbContext has DbSet<AuditEntry> AuditEntries (table name = property name)
        tableNames.Should().Contain("AuditEntries",
            "AuditDbContext has DbSet<AuditEntry> AuditEntries property — table name = property name");
    }
}
