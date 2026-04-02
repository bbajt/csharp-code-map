Namespace Models

    ''' <summary>Base contract for all domain entities.</summary>
    Public Interface IEntity
        ReadOnly Property Id As Integer
        ReadOnly Property CreatedAt As DateTime
    End Interface

End Namespace
