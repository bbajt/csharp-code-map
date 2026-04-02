Imports System.Threading
Imports System.Threading.Tasks
Imports SampleVbApp.Models
Imports SampleVbApp.Services
Imports SampleVbApp.Stubs

Namespace Api

    ''' <summary>REST API controller for order operations.</summary>
    <ApiController>
    <Route("api/[controller]")>
    Public Class OrdersController

        Private ReadOnly _orderService As IOrderService

        Public Sub New(orderService As IOrderService)
            _orderService = orderService
        End Sub

        ''' <summary>Retrieve a single order by ID.</summary>
        <HttpGet("{id}")>
        Public Async Function GetOrder(id As Integer, Optional ct As CancellationToken = Nothing) As Task(Of Order)
            Return Await _orderService.GetOrderAsync(id, ct)
        End Function

        ''' <summary>Submit a new order.</summary>
        <HttpPost>
        Public Async Function SubmitOrder(order As Order, Optional ct As CancellationToken = Nothing) As Task(Of Boolean)
            Return Await _orderService.SubmitOrderAsync(order, ct)
        End Function

        ''' <summary>Cancel an existing order.</summary>
        <HttpDelete("{id}")>
        Public Async Function CancelOrder(id As Integer, Optional ct As CancellationToken = Nothing) As Task
            Await _orderService.CancelOrderAsync(id, ct)
        End Function

    End Class

End Namespace
