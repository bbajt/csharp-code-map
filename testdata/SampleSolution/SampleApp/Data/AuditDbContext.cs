using Microsoft.EntityFrameworkCore;
using SampleApp.Models;

namespace SampleApp.Data;

/// <summary>Entity Framework DbContext for the audit trail — AC-T01-09 multiple DbContexts.</summary>
public class AuditDbContext : DbContext
{
    public DbSet<AuditEntry> AuditEntries { get; set; } = null!;
}
