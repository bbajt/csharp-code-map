using Microsoft.EntityFrameworkCore;
using SampleApp.Models;

namespace SampleApp.Data;

/// <summary>EF Core DbContext for the sample application.</summary>
public class AppDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; } = null!;
}
