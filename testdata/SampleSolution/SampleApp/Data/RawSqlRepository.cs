namespace SampleApp.Data;

/// <summary>
/// Demonstrates raw SQL execution patterns.
/// Exists to exercise DbTableExtractor pattern 3 (SQL method invocations)
/// with multi-line SQL constants from RawSqlQueries.
/// </summary>
public class RawSqlRepository
{
    private readonly OrderDbContext _orderDb;
    private readonly AuditDbContext _auditDb;

    public RawSqlRepository(OrderDbContext orderDb, AuditDbContext auditDb)
    {
        _orderDb = orderDb;
        _auditDb = auditDb;
    }

    public IReadOnlyList<string> GetOrdersByStatus(string status)
    {
        // Pattern 3: ExecuteSqlRaw with multi-line SQL constant (FROM Orders, INNER JOIN Customers)
        _orderDb.Database.ExecuteSqlRaw(RawSqlQueries.GetOrdersByStatus, status);
        return [];
    }

    public int InsertAuditEntry(string entityId, string action, string userId)
    {
        // Pattern 3: ExecuteSqlRaw with multi-line SQL constant (INSERT INTO AuditLog)
        return _auditDb.Database.ExecuteSqlRaw(
            RawSqlQueries.InsertAuditLog, entityId, action, DateTime.UtcNow, userId);
    }

    public int UpdateOrderStatus(int id, string status)
    {
        // Pattern 3: ExecuteSqlRaw with multi-line SQL constant (UPDATE Orders)
        return _orderDb.Database.ExecuteSqlRaw(
            RawSqlQueries.UpdateOrderStatus, status, DateTime.UtcNow, id);
    }

    public int CountByStatus(string status)
    {
        // Pattern 3: ExecuteSqlRaw with single-line SQL constant (FROM Orders)
        return _orderDb.Database.ExecuteSqlRaw(RawSqlQueries.CountOrders, status);
    }
}
