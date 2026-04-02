Namespace Models

    ''' <summary>Immutable value type for monetary amounts.</summary>
    Public Structure Money
        Public ReadOnly Property Amount As Decimal
        Public ReadOnly Property Currency As String

        Public Sub New(amount As Decimal, currency As String)
            Me.Amount = amount
            Me.Currency = currency
        End Sub

        Public Shared Operator +(a As Money, b As Money) As Money
            Return New Money(a.Amount + b.Amount, a.Currency)
        End Operator

        Public Overrides Function ToString() As String
            Return $"{Amount} {Currency}"
        End Function
    End Structure

End Namespace
