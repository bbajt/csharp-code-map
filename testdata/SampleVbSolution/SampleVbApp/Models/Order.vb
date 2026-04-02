Namespace Models

    ''' <summary>Represents a customer order.</summary>
    Public Class Order
        Implements IEntity

        Public ReadOnly Property Id As Integer Implements IEntity.Id
        Public ReadOnly Property CreatedAt As DateTime Implements IEntity.CreatedAt
        Public Property CustomerId As Integer
        Public Property Status As OrderStatus
        Public Property Total As Money
        Public Property Items As New List(Of String)

        Public Sub New(id As Integer, customerId As Integer)
            Me.Id = id
            Me.CustomerId = customerId
            Me.CreatedAt = DateTime.UtcNow
            Me.Status = OrderStatus.Pending
        End Sub

        Public Sub AddItem(item As String)
            Items.Add(item)
        End Sub
    End Class

End Namespace
