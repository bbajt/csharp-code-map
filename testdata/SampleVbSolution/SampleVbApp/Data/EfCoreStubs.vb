Namespace Stubs

    ''' <summary>Minimal EF Core stubs so classlib compiles without real EF NuGet.</summary>
    Public Class DbContext
        Public Overridable Sub OnModelCreating(builder As Object)
        End Sub
    End Class

    Public Class DbSet(Of T)
    End Class

End Namespace
