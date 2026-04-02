Namespace Models

    ''' <summary>Represents a customer in the system.</summary>
    Public Class Customer
        Implements IEntity

        Public ReadOnly Property Id As Integer Implements IEntity.Id
        Public ReadOnly Property CreatedAt As DateTime Implements IEntity.CreatedAt
        Public Property Name As String
        Public Property Email As String
        Public Property IsActive As Boolean

        Public Sub New(id As Integer, name As String, email As String)
            Me.Id = id
            Me.Name = name
            Me.Email = email
            Me.IsActive = True
            Me.CreatedAt = DateTime.UtcNow
        End Sub
    End Class

End Namespace
