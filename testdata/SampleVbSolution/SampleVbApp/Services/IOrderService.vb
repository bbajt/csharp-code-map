Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Order management contract.</summary>
    Public Interface IOrderService
        Function GetOrderAsync(orderId As Integer, Optional ct As CancellationToken = Nothing) As Task(Of Models.Order)
        Function SubmitOrderAsync(order As Models.Order, Optional ct As CancellationToken = Nothing) As Task(Of Boolean)
        Function CancelOrderAsync(orderId As Integer, Optional ct As CancellationToken = Nothing) As Task
    End Interface

End Namespace
