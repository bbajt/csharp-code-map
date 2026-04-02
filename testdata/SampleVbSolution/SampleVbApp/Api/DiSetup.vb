Imports SampleVbApp.Services
Imports SampleVbApp.Stubs
Imports SampleVbApp.Stubs.Di

Namespace Api

    ''' <summary>DI registration helpers for SampleVbApp services.</summary>
    Public Module DiSetup

        <System.Runtime.CompilerServices.Extension>
        Public Function AddSampleVbServices(services As IServiceCollection) As IServiceCollection
            services.AddScoped(Of IOrderService, OrderService)()
            services.AddSingleton(Of IOrderService)()
            Return services
        End Function

    End Module

End Namespace
