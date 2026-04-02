Namespace Utilities

    ''' <summary>Extension methods for String.</summary>
    Public Module StringExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Function ToTitleCase(value As String) As String
            If String.IsNullOrEmpty(value) Then Return value
            Return Char.ToUpper(value(0)) & value.Substring(1).ToLower()
        End Function

        <System.Runtime.CompilerServices.Extension>
        Public Function Truncate(value As String, maxLength As Integer) As String
            If value.Length <= maxLength Then Return value
            Return value.Substring(0, maxLength) & "..."
        End Function

    End Module

End Namespace
