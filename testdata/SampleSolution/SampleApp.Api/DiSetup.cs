using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SampleApp.Api.HealthChecks;
using SampleApp.Api.Services;
using SampleApp.Services;

namespace SampleApp.Api;

public static class DiSetup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Pattern 1: Generic pair (AddScoped<Interface, Impl>)
        services.AddScoped<IOrderService, OrderService>();

        // Pattern 2: Self-registration (AddSingleton<T>)
        services.AddSingleton<OrderService>();

        // Pattern 3: Factory lambda (AddSingleton<I>(sp => new T(...)))
        services.AddSingleton<IPaymentGateway>(sp =>
            new StripePaymentGateway(sp.GetRequiredService<IConfiguration>()));

        // Pattern 4: TryAdd (TryAddScoped<I,T>)
        services.TryAddScoped<INotificationService, NotificationService>();

        // Pattern 5: AddHostedService<T>
        services.AddHostedService<OrderCleanupBackgroundService>();

        // Pattern 6: Instance argument (new expression)
        services.AddSingleton<ICachePolicy>(new SlidingCachePolicy(TimeSpan.FromMinutes(5)));

        // Pattern 7: Inferred-type factory (no explicit type arg — Roslyn infers PrometheusMetricsCollector)
        services.AddSingleton(sp => new PrometheusMetricsCollector());

        // Health checks
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database");
    }
}
