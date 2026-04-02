namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;

public class VbDbTableExtractorTests
{
    private const string EfStubs = """
        Public Class DbContext : End Class
        Public Class DbSet(Of T) : End Class
        Public Class Order : End Class
        Public Class Customer : End Class
        """;

    private const string TableAttrStub = """
        Imports System
        <AttributeUsage(AttributeTargets.Class)>
        Public Class TableAttribute : Inherits Attribute
            Public Sub New(name As String) : End Sub
        End Class
        """;

    private static Compilation CreateCompilation(string source)
        => CompilationBuilder.CreateVb("TestVb", EfStubs, source);

    private static Compilation CreateCompilationWithTable(string source)
        => CompilationBuilder.CreateVb("TestVb", TableAttrStub, source);

    [Fact]
    public void ExtractsDbSetProperties()
    {
        const string source = """
            Public Class AppDbContext : Inherits DbContext
                Public Property Orders As DbSet(Of Order)
                Public Property Customers As DbSet(Of Customer)
            End Class
            """;

        var facts = VbDbTableExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().Contain(f => f.Kind == FactKind.DbTable && f.Value.StartsWith("Orders|DbSet<Order>"));
        facts.Should().Contain(f => f.Kind == FactKind.DbTable && f.Value.StartsWith("Customers|DbSet<Customer>"));
    }

    [Fact]
    public void ExtractsTableAttribute()
    {
        const string source = """
            <Table("tbl_products")>
            Public Class Product
                Public Property Id As Integer
            End Class
            """;

        var facts = VbDbTableExtractor.ExtractAll(CreateCompilationWithTable(source), "");

        facts.Should().ContainSingle(f => f.Value == "tbl_products|[Table]");
    }

    [Fact]
    public void IgnoresNonDbContextClasses()
    {
        const string source = """
            Public Class NotADbContext
                Public Property Foo As String
            End Class
            """;

        var facts = VbDbTableExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().BeEmpty();
    }

    [Fact]
    public void ExtractsFullPropertyBlockDbSet()
    {
        // MINOR-1 regression: full property with getter/setter (PropertyBlockSyntax) must
        // be handled in addition to auto-properties (PropertyStatementSyntax).
        const string source = """
            Public Class AppDbContext : Inherits DbContext
                Private _orders As DbSet(Of Order)
                Public Property Orders As DbSet(Of Order)
                    Get
                        Return _orders
                    End Get
                    Set(value As DbSet(Of Order))
                        _orders = value
                    End Set
                End Property
            End Class
            """;

        var facts = VbDbTableExtractor.ExtractAll(CreateCompilation(source), "");

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.DbTable && f.Value.StartsWith("Orders|DbSet<Order>"));
    }
}
