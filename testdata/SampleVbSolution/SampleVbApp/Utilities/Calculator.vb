Namespace Utilities

    ''' <summary>Stateless calculation helpers (VB.NET Module = C# static class).</summary>
    Public Module Calculator

        ''' <summary>Calculates the tax amount for a given subtotal.</summary>
        Public Function CalculateTax(subtotal As Decimal, rate As Decimal) As Decimal
            Return subtotal * rate
        End Function

        ''' <summary>Rounds a monetary amount to two decimal places.</summary>
        Public Function RoundMoney(amount As Decimal) As Decimal
            Return Math.Round(amount, 2, MidpointRounding.AwayFromZero)
        End Function

    End Module

End Namespace
