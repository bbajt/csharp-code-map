Imports SampleVbApp.Models
Imports SampleVbApp.Stubs

Namespace Data

    ''' <summary>Main application DbContext for EF Core.</summary>
    Public Class AppDbContext
        Inherits DbContext

        Public Property Orders As DbSet(Of Order)
        Public Property Customers As DbSet(Of Customer)

    End Class

End Namespace
