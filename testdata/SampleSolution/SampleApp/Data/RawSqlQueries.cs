namespace SampleApp.Data;

/// <summary>Raw SQL query constants for direct database access operations.</summary>
public static class RawSqlQueries
{
    // Multi-line verbatim string — AC-T01-03 regression for GAP-01
    public const string GetOrdersByStatus = @"
        SELECT o.Id, o.CustomerId, o.Total, o.Status
        FROM Orders o
        INNER JOIN Customers c ON o.CustomerId = c.Id
        WHERE o.Status = @status
        ORDER BY o.CreatedAt DESC";

    // Multi-line INSERT
    public const string InsertAuditLog = @"
        INSERT INTO AuditLog
            (EntityId, Action, Timestamp, UserId)
        VALUES
            (@entityId, @action, @timestamp, @userId)";

    // UPDATE across multiple lines
    public const string UpdateOrderStatus = @"
        UPDATE Orders
        SET Status = @status,
            UpdatedAt = @updatedAt
        WHERE Id = @id";

    // Single-line (regression — existing pattern must still work)
    public const string CountOrders = "SELECT COUNT(*) FROM Orders WHERE Status = @s";
}
