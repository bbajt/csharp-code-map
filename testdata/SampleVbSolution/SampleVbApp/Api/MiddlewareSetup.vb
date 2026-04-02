Imports SampleVbApp.Stubs
Imports SampleVbApp.Stubs.Di

Namespace Api

    ''' <summary>Demonstrates middleware pipeline registration for VB.NET.</summary>
    Public Module MiddlewareSetup

        <System.Runtime.CompilerServices.Extension>
        Public Function ConfigurePipeline(app As IApplicationBuilder) As IApplicationBuilder
            app.UseRouting()
            app.UseAuthentication()
            app.UseAuthorization()
            Return app
        End Function

    End Module

End Namespace
