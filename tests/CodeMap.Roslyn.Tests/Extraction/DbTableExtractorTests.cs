namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class DbTableExtractorTests
{
    // Minimal EF Core stubs for in-memory compilation.
    // DbTableExtractor uses name-based type detection (Name == "DbSet", Name == "DbContext")
    // so stubs with matching type names in the right namespace work correctly.
    private const string EfCoreStubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { }

            public class DbSet<T> where T : class { }
        }
        """;

    // [Table] attribute stub (from System.ComponentModel.DataAnnotations.Schema)
    private const string TableAttributeStub = """
        namespace System.ComponentModel.DataAnnotations.Schema
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class TableAttribute : System.Attribute
            {
                public TableAttribute(string name) { Name = name; }
                public string Name { get; }
                public string? Schema { get; set; }
            }
        }
        """;

    private const string AllStubs = EfCoreStubs + "\n" + TableAttributeStub;

    // Stub for SQL execution method
    private const string SqlExecutorStub = """
        namespace TestApp.Data
        {
            public class DatabaseFacade
            {
                public void ExecuteSqlRaw(string sql, params object[] args) { }
            }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(
        string source,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var compilation = CompilationBuilder.Create(AllStubs, source);
        return DbTableExtractor.ExtractAll(compilation, "/repo/", stableIdMap);
    }

    // ── Pattern 1: DbSet<T> properties ───────────────────────────────────────

    [Fact]
    public void Extract_DbSetProperty_ProducesDbTableFact()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Models { public class Order { } }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.DbTable &&
            f.Value.StartsWith("Orders|DbSet<", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_DbSetProperty_TableNameIsPropertyName()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Models { public class Order { } }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        // The table name prefix (before |) should be "Orders"
        var fact = facts.Single(f => f.Kind == FactKind.DbTable);
        var tableName = fact.Value[..fact.Value.IndexOf('|', StringComparison.Ordinal)];
        tableName.Should().Be("Orders");
    }

    [Fact]
    public void Extract_DbSetProperty_ConfidenceIsHigh()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Models { public class Order { } }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        facts.Single(f => f.Kind == FactKind.DbTable).Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public void Extract_TableAttribute_OverridesPropertyName()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace TestApp.Models
            {
                [Table("tbl_orders")]
                public class Order { }
            }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        // Should use "tbl_orders" not "Orders"
        var dbSetFact = facts.Single(f => f.Kind == FactKind.DbTable && f.Value.Contains("DbSet<"));
        dbSetFact.Value.Should().StartWith("tbl_orders|");
    }

    [Fact]
    public void Extract_TableAttribute_WithSchema_IncludesSchemaPrefix()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace TestApp.Models
            {
                [Table("tbl_orders", Schema = "sales")]
                public class Order { }
            }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        var dbSetFact = facts.Single(f => f.Kind == FactKind.DbTable && f.Value.Contains("DbSet<"));
        dbSetFact.Value.Should().StartWith("sales.tbl_orders|");
    }

    [Fact]
    public void Extract_StandaloneTableAttribute_ExtractedDirectly()
    {
        // Entity with [Table] but no corresponding DbContext in this compilation
        const string source = """
            using System.ComponentModel.DataAnnotations.Schema;

            namespace TestApp.Models
            {
                [Table("audit_log")]
                public class AuditLog { }
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.DbTable &&
            f.Value == "audit_log|[Table]");
    }

    [Fact]
    public void Extract_DbSetEntityWithTableAttribute_NotDuplicatedAsStandalone()
    {
        // Order has [Table] AND is in DbSet<T> — should produce exactly 1 fact (DbSet wins)
        const string source = """
            using Microsoft.EntityFrameworkCore;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace TestApp.Models
            {
                [Table("tbl_orders")]
                public class Order { }
            }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        // Should have exactly 1 DbTable fact (the DbSet one, not a duplicate [Table] one)
        facts.Count(f => f.Kind == FactKind.DbTable).Should().Be(1);
    }

    [Fact]
    public void Extract_NonDbSetProperty_Ignored()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp.Models { public class Order { } }

            namespace TestApp.Services
            {
                using TestApp.Models;

                public class OrderService
                {
                    public List<Order> ActiveOrders { get; set; } = new();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MultipleDbSets_ProducesMultipleFacts()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Models
            {
                public class Order { }
                public class Customer { }
                public class Product { }
            }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order>    Orders    { get; set; } = null!;
                    public DbSet<Customer> Customers { get; set; } = null!;
                    public DbSet<Product>  Products  { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        facts.Count(f => f.Kind == FactKind.DbTable).Should().Be(3);
    }

    // ── Pattern 3: Raw SQL strings ────────────────────────────────────────────

    [Fact]
    public void Extract_RawSql_FromStatement_ExtractsTableName()
    {
        const string source = """
            using TestApp.Data;

            namespace TestApp.Services
            {
                public class OrderRepository
                {
                    private readonly DatabaseFacade _db = null!;

                    public void GetActiveOrders()
                    {
                        _db.ExecuteSqlRaw("SELECT * FROM dbo.Orders WHERE Status = 1");
                    }
                }
            }
            """ + "\n" + SqlExecutorStub;

        var compilation = CompilationBuilder.Create(AllStubs, source);
        var facts = DbTableExtractor.ExtractAll(compilation, "/repo/");

        facts.Should().Contain(f =>
            f.Kind == FactKind.DbTable &&
            f.Value == "dbo.Orders|Raw SQL" &&
            f.Confidence == Confidence.Medium);
    }

    [Fact]
    public void Extract_RawSql_MultipleTablesFromJoin_ExtractsBoth()
    {
        const string source = """
            using TestApp.Data;

            namespace TestApp.Services
            {
                public class ReportService
                {
                    private readonly DatabaseFacade _db = null!;

                    public void GetOrderItems()
                    {
                        _db.ExecuteSqlRaw("SELECT o.Id, i.Name FROM Orders o JOIN OrderItems i ON o.Id = i.OrderId");
                    }
                }
            }
            """ + "\n" + SqlExecutorStub;

        var compilation = CompilationBuilder.Create(AllStubs, source);
        var facts = DbTableExtractor.ExtractAll(compilation, "/repo/");

        var tableNames = facts
            .Where(f => f.Kind == FactKind.DbTable && f.Value.EndsWith("|Raw SQL", StringComparison.Ordinal))
            .Select(f => f.Value[..f.Value.IndexOf('|', StringComparison.Ordinal)])
            .ToList();

        tableNames.Should().Contain("Orders");
        tableNames.Should().Contain("OrderItems");
    }

    // ── StableId and SymbolId ─────────────────────────────────────────────────

    [Fact]
    public void Extract_StableId_PopulatedFromMap()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Models { public class Order { } }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var stableId = new StableId("sym_abcdef0123456789");
        // We need to find the actual symbolId key — extract without map first
        var factsNoMap = Extract(source);
        var symbolId = factsNoMap.Single(f => f.Kind == FactKind.DbTable).SymbolId;
        var map = new Dictionary<string, StableId> { [symbolId.Value] = stableId };

        var facts = Extract(source, map);

        facts.Single(f => f.Kind == FactKind.DbTable).StableId.Should().Be(stableId);
    }

    [Fact]
    public void Extract_HandlerSymbol_PointsToDbSetProperty()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Models { public class Order { } }

            namespace TestApp.Data
            {
                using TestApp.Models;

                public class AppDbContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; } = null!;
                }
            }
            """;

        var facts = Extract(source);

        var fact = facts.Single(f => f.Kind == FactKind.DbTable);
        // Symbol ID should reference the Orders property (P: prefix in doc comment ID)
        fact.SymbolId.Value.Should().NotBeNullOrEmpty();
        fact.SymbolId.Value.Should().Contain("Orders");
    }
}
