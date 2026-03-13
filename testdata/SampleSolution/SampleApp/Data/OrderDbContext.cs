using Microsoft.EntityFrameworkCore;
using SampleApp.Models;

namespace SampleApp.Data;

/// <summary>Entity Framework DbContext for order management data — AC-T01-09 multiple DbContexts.</summary>
public class OrderDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
}
