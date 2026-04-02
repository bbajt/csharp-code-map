Imports SampleVbApp.Stubs

Namespace Api

    ''' <summary>Demonstrates IConfiguration usage patterns for VB.NET.</summary>
    Public Class ConfigConsumer

        Private ReadOnly _config As IConfiguration

        Public Sub New(config As IConfiguration)
            _config = config
        End Sub

        Public Function GetConnectionString() As String
            Return _config.GetItem("ConnectionStrings:Default")
        End Function

        Public Function GetMaxRetries() As Integer
            Return _config.GetValue(Of Integer)("App:MaxRetries")
        End Function

        Public Function GetFeatureSection() As IConfiguration
            Return _config.GetSection("Features")
        End Function

    End Class

End Namespace
