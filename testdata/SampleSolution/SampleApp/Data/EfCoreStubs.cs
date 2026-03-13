// Minimal EF Core stubs for SampleSolution.
// SampleApp is a classlib that does not reference the real Microsoft.EntityFrameworkCore package.
// These stubs allow AppDbContext.cs to compile without a NuGet dependency.
// The DbTableExtractor uses name-based type detection and works with these stubs.
namespace Microsoft.EntityFrameworkCore;

public class DatabaseFacade
{
    public int ExecuteSqlRaw(string sql, params object[] parameters) => 0;
    public int ExecuteSqlInterpolated(FormattableString sql) => 0;
}

public class DbContext
{
    public DatabaseFacade Database { get; } = new DatabaseFacade();
}

public class DbSet<T> where T : class { }
