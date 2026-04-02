Imports System.Threading.Tasks
Imports SampleVbApp.Stubs

Namespace Api

    ''' <summary>Demonstrates all ILogger call patterns for VB.NET.</summary>
    Public Class LoggingExample

        Private ReadOnly _logger As ILogger(Of LoggingExample)

        Public Sub New(logger As ILogger(Of LoggingExample))
            _logger = logger
        End Sub

        Public Async Function ProcessAsync() As Task
            _logger.LogDebug("Starting processing")
            Try
                Await Task.Delay(1)
                _logger.LogInformation("Processing completed for {ItemCount} items", 42)
            Catch ex As Exception
                _logger.LogError("Processing failed: {Message}", ex.Message)
                Throw New InvalidOperationException("Processing error", ex)
            End Try
        End Function

    End Class

End Namespace
