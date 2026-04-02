''' <summary>Minimal ASP.NET / DI / Configuration / Logging stubs for classlib compilation.</summary>

Namespace Stubs

    Public Interface IConfiguration
        Function GetItem(key As String) As String
        Function GetSection(key As String) As IConfiguration
        Function GetValue(Of T)(key As String) As T
    End Interface

    Public Interface IServiceCollection
    End Interface

    Public Interface ILogger(Of T)
        Sub LogInformation(message As String, ParamArray args As Object())
        Sub LogWarning(message As String, ParamArray args As Object())
        Sub LogError(message As String, ParamArray args As Object())
        Sub LogDebug(message As String, ParamArray args As Object())
    End Interface

    Public Interface IApplicationBuilder
    End Interface

    <AttributeUsage(AttributeTargets.Class)>
    Public Class ApiControllerAttribute : Inherits Attribute : End Class

    <AttributeUsage(AttributeTargets.Class Or AttributeTargets.Method)>
    Public Class RouteAttribute : Inherits Attribute
        Public Sub New(template As String)
        End Sub
    End Class

    <AttributeUsage(AttributeTargets.Method)>
    Public Class HttpGetAttribute : Inherits Attribute
        Public Sub New(Optional template As String = "")
        End Sub
    End Class

    <AttributeUsage(AttributeTargets.Method)>
    Public Class HttpPostAttribute : Inherits Attribute
        Public Sub New(Optional template As String = "")
        End Sub
    End Class

    <AttributeUsage(AttributeTargets.Method)>
    Public Class HttpPutAttribute : Inherits Attribute
        Public Sub New(Optional template As String = "")
        End Sub
    End Class

    <AttributeUsage(AttributeTargets.Method)>
    Public Class HttpDeleteAttribute : Inherits Attribute
        Public Sub New(Optional template As String = "")
        End Sub
    End Class

    <AttributeUsage(AttributeTargets.Class)>
    Public Class TableAttribute : Inherits Attribute
        Public Sub New(name As String)
        End Sub
    End Class

End Namespace

Namespace Stubs.Di

    Public Module ServiceCollectionExtensions
        <System.Runtime.CompilerServices.Extension()>
        Public Function AddScoped(Of TService, TImpl)(services As SampleVbApp.Stubs.IServiceCollection) As SampleVbApp.Stubs.IServiceCollection
            Return services
        End Function

        <System.Runtime.CompilerServices.Extension()>
        Public Function AddScoped(Of TService)(services As SampleVbApp.Stubs.IServiceCollection) As SampleVbApp.Stubs.IServiceCollection
            Return services
        End Function

        <System.Runtime.CompilerServices.Extension()>
        Public Function AddSingleton(Of TService)(services As SampleVbApp.Stubs.IServiceCollection) As SampleVbApp.Stubs.IServiceCollection
            Return services
        End Function

        <System.Runtime.CompilerServices.Extension()>
        Public Function AddTransient(Of TService, TImpl)(services As SampleVbApp.Stubs.IServiceCollection) As SampleVbApp.Stubs.IServiceCollection
            Return services
        End Function
    End Module

    Public Module AppBuilderExtensions
        <System.Runtime.CompilerServices.Extension()>
        Public Function UseAuthentication(app As SampleVbApp.Stubs.IApplicationBuilder) As SampleVbApp.Stubs.IApplicationBuilder
            Return app
        End Function

        <System.Runtime.CompilerServices.Extension()>
        Public Function UseAuthorization(app As SampleVbApp.Stubs.IApplicationBuilder) As SampleVbApp.Stubs.IApplicationBuilder
            Return app
        End Function

        <System.Runtime.CompilerServices.Extension()>
        Public Function UseRouting(app As SampleVbApp.Stubs.IApplicationBuilder) As SampleVbApp.Stubs.IApplicationBuilder
            Return app
        End Function
    End Module

End Namespace
