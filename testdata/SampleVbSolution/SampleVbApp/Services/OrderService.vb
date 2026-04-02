Imports System.Threading
Imports System.Threading.Tasks
Imports SampleVbApp.Models
Imports SampleVbApp.Stubs

Namespace Services

    ''' <summary>Default implementation of IOrderService.</summary>
    Public Class OrderService
        Implements IOrderService

        Private ReadOnly _logger As ILogger(Of OrderService)

        Public Sub New(logger As ILogger(Of OrderService))
            _logger = logger
        End Sub

        Public Async Function GetOrderAsync(orderId As Integer, Optional ct As CancellationToken = Nothing) As Task(Of Order) Implements IOrderService.GetOrderAsync
            _logger.LogInformation("Fetching order {OrderId}", orderId)
            Await Task.Delay(1, ct)
            Return New Order(orderId, 1)
        End Function

        Public Async Function SubmitOrderAsync(order As Order, Optional ct As CancellationToken = Nothing) As Task(Of Boolean) Implements IOrderService.SubmitOrderAsync
            If order Is Nothing Then
                Throw New ArgumentNullException(NameOf(order))
            End If
            _logger.LogInformation("Submitting order {OrderId}", order.Id)
            Await Task.Delay(1, ct)
            order.Status = OrderStatus.Processing
            Return True
        End Function

        Public Async Function CancelOrderAsync(orderId As Integer, Optional ct As CancellationToken = Nothing) As Task Implements IOrderService.CancelOrderAsync
            _logger.LogWarning("Cancelling order {OrderId}", orderId)
            Await Task.Delay(1, ct)
        End Function
    End Class

End Namespace
